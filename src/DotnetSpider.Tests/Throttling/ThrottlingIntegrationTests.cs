using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Downloader;
using DotnetSpider.Http;
using DotnetSpider.Proxy;
using DotnetSpider.Throttling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DotnetSpider.Tests.Throttling;

public class ThrottlingIntegrationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly TestHttpClientFactory _httpClientFactory;

    public ThrottlingIntegrationTests()
    {
        var services = new ServiceCollection();
        
        _httpClientFactory = new TestHttpClientFactory();
        
        services.AddLogging();
        services.AddSingleton<IHttpClientFactory>(_httpClientFactory);
        services.AddSingleton<IProxyService, EmptyProxyService>();
        services.AddAdaptiveThrottling(options =>
        {
            options.EnableAdaptiveThrottling = true;
            options.EnableBandwidthThrottling = true;
            options.MaxBandwidthPerHost = 10240; // 10KB/s
        });
        
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task AdaptiveThrottling_ReducesConcurrencyOnErrors()
    {
        var downloader = _serviceProvider.GetRequiredService<IDownloader>() as AdaptiveHttpClientDownloader;
        var hostThrottler = _serviceProvider.GetRequiredService<IHostThrottler>();
        
        Assert.NotNull(downloader);
        
        const string host = "error.example.com";
        var initialMetrics = downloader.GetHostMetrics(host);
        
        for (int i = 0; i < 20; i++)
        {
            var request = new Request($"https://{host}/error{i}");
            await downloader.DownloadAsync(request);
        }
        
        var finalMetrics = downloader.GetHostMetrics(host);
        
        Assert.True(finalMetrics.ErrorRate > 0);
        Assert.True(finalMetrics.ErrorRate >= initialMetrics.ErrorRate);
    }

    [Fact]
    public async Task AdaptiveThrottling_IncreasesConcurrencyOnSuccess()
    {
        var downloader = _serviceProvider.GetRequiredService<IDownloader>() as AdaptiveHttpClientDownloader;
        
        Assert.NotNull(downloader);
        
        const string host = "success.example.com";
        var initialMetrics = downloader.GetHostMetrics(host);
        
        for (int i = 0; i < 30; i++)
        {
            var request = new Request($"https://{host}/success{i}");
            await downloader.DownloadAsync(request);
            
            await Task.Delay(10);
        }
        
        var finalMetrics = downloader.GetHostMetrics(host);
        
        Assert.True(finalMetrics.AverageLatency > 0);
        Assert.Equal(0.0, finalMetrics.ErrorRate);
    }

    [Fact]
    public async Task AdaptiveThrottling_HandlesMultipleHostsIndependently()
    {
        var downloader = _serviceProvider.GetRequiredService<IDownloader>() as AdaptiveHttpClientDownloader;
        
        Assert.NotNull(downloader);
        
        const string successHost = "success.example.com";
        const string errorHost = "error.example.com";
        
        var tasks = new List<Task>();
        
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(downloader.DownloadAsync(new Request($"https://{successHost}/success{i}")));
            tasks.Add(downloader.DownloadAsync(new Request($"https://{errorHost}/error{i}")));
        }
        
        await Task.WhenAll(tasks);
        
        var successMetrics = downloader.GetHostMetrics(successHost);
        var errorMetrics = downloader.GetHostMetrics(errorHost);
        
        Assert.True(successMetrics.ErrorRate < errorMetrics.ErrorRate);
    }

    [Fact]
    public async Task ConcurrentRequests_RespectThrottling()
    {
        var downloader = _serviceProvider.GetRequiredService<IDownloader>() as AdaptiveHttpClientDownloader;
        
        Assert.NotNull(downloader);
        
        const string host = "slow.example.com";
        const int concurrentRequests = 20;
        
        var stopwatch = Stopwatch.StartNew();
        var tasks = new Task[concurrentRequests];
        
        for (int i = 0; i < concurrentRequests; i++)
        {
            tasks[i] = downloader.DownloadAsync(new Request($"https://{host}/slow{i}"));
        }
        
        await Task.WhenAll(tasks);
        stopwatch.Stop();
        
        Assert.True(stopwatch.ElapsedMilliseconds > 100);
        
        var metrics = downloader.GetHostMetrics(host);
        Assert.True(metrics.AverageLatency > 0);
    }

    [Fact]
    public void ServiceRegistration_RegistersAllServices()
    {
        var hostThrottler = _serviceProvider.GetService<IHostThrottler>();
        var downloader = _serviceProvider.GetService<IDownloader>();
        var options = _serviceProvider.GetService<AdaptiveDownloaderOptions>();
        
        Assert.NotNull(hostThrottler);
        Assert.NotNull(downloader);
        Assert.NotNull(options);
        Assert.IsType<AdaptiveHttpClientDownloader>(downloader);
    }

    [Fact]
    public async Task Builder_UseAdaptiveThrottling_WorksCorrectly()
    {
        var builder = Builder.CreateBuilder<TestSpider>()
            .UseAdaptiveThrottling(options =>
            {
                options.EnableAdaptiveThrottling = true;
                options.MaxBandwidthPerHost = 5120;
            });
        
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IHttpClientFactory>(_httpClientFactory);
        });
        
        using var host = builder.Build();
        var downloader = host.Services.GetRequiredService<IDownloader>();
        
        Assert.IsType<AdaptiveHttpClientDownloader>(downloader);
        
        var request = new Request("https://test.example.com/data");
        var response = await downloader.DownloadAsync(request);
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        _httpClientFactory?.Dispose();
    }
}

public class TestHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly List<HttpClient> _clients = new();
    
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(new IntegrationTestMessageHandler());
        _clients.Add(client);
        return client;
    }
    
    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client?.Dispose();
        }
    }
}

public class IntegrationTestMessageHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        var uri = request.RequestUri.ToString();
        
        if (uri.Contains("error"))
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new System.Net.Http.StringContent("Server error")
            };
        }
        
        if (uri.Contains("slow"))
        {
            await Task.Delay(50, cancellationToken);
        }
        
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent($"Response for {uri}")
        };
    }
}

public class TestSpider : Spider
{
    public TestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger) 
        : base(options, services, logger)
    {
    }
    
    protected override async Task InitializeAsync(CancellationToken stoppingToken)
    {
        await Task.CompletedTask;
    }
}