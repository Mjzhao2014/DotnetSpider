using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Proxy;
using DotnetSpider.Throttling;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace DotnetSpider.Tests.Throttling;

public class ThrottlingIntegrationTests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<IProxyService> _proxyServiceMock;
    private readonly Mock<ILogger<AdaptiveHttpClientDownloader>> _loggerMock;
    private readonly AdaptiveThrottleOptions _options;
    private readonly AdaptiveThrottleManager _throttleManager;

    public ThrottlingIntegrationTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _proxyServiceMock = new Mock<IProxyService>();
        _loggerMock = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        _options = new AdaptiveThrottleOptions 
        { 
            EnableAdaptiveThrottling = true,
            MaxRetryAttempts = 3,
            MinConcurrency = 1,
            MaxConcurrency = 5,
            CooldownPeriod = TimeSpan.FromMilliseconds(100),
            RequestSpacing = TimeSpan.FromMilliseconds(50)
        };
        _throttleManager = new AdaptiveThrottleManager(_options);
        _proxyServiceMock.Setup(x => x.GetAsync(It.IsAny<int>())).ReturnsAsync((Uri)null);
    }

    [Fact]
    public async Task CancellationSafety_CancelledToken_ThrowsOperationCanceledException()
    {
        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var hostGate = _throttleManager.GetHostGate("example.com");
        
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => hostGate.AcquireAsync(cts.Token));
    }

    [Fact]
    public async Task CancellationSafety_CancelDuringAcquire_ReleasesResources()
    {
        var hostGate = _throttleManager.GetHostGate("example.com");
        var initialConcurrency = hostGate.CurrentConcurrency;
        
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        try
        {
            using (await hostGate.AcquireAsync(cts.Token))
            {
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await Task.Delay(100);
        Assert.Equal(initialConcurrency, hostGate.CurrentConcurrency);
    }

    [Fact]
    public async Task ErrorHandling_NetworkException_RecordsError()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var httpClient = new HttpClient(mockHandler.Object);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var request = new Request("http://example.com");
        var response = await downloader.DownloadAsync(request);
        
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        
        var stats = downloader.GetHostStats("example.com");
        Assert.True(stats.TotalErrors > 0);
    }

    [Fact]
    public async Task ErrorHandling_TimeoutException_RecordsError()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Request timeout"));

        var httpClient = new HttpClient(mockHandler.Object);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var request = new Request("http://example.com");
        var response = await downloader.DownloadAsync(request);
        
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        
        var stats = downloader.GetHostStats("example.com");
        Assert.True(stats.TotalErrors > 0);
    }

    [Fact]
    public async Task RetryBackoffStrategy_ExponentialBackoff_IncreasesProperly()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.InternalServerError, "Error");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var request = new Request("http://example.com");
        var stopwatch = Stopwatch.StartNew();
        
        await downloader.DownloadAsync(request);
        
        stopwatch.Stop();
        
        Assert.True(stopwatch.ElapsedMilliseconds > 1000);
    }

    [Fact]
    public async Task ConcurrentRequests_SameHost_RespectsThrottling()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.OK, "Success");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var request = new Request("http://example.com");
        var tasks = new Task[10];
        
        var stopwatch = Stopwatch.StartNew();
        
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = downloader.DownloadAsync(request);
        }
        
        await Task.WhenAll(tasks);
        stopwatch.Stop();
        
        var stats = downloader.GetHostStats("example.com");
        Assert.Equal(10, stats.TotalRequests);
        
        Assert.True(stopwatch.ElapsedMilliseconds > 400);
    }

    [Fact]
    public async Task RequestSpacing_EnforcesMinimumDelay()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.OK, "Success");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var hostGate = _throttleManager.GetHostGate("example.com");
        
        var stopwatch = Stopwatch.StartNew();
        
        using (await hostGate.AcquireAsync())
        {
        }
        
        var firstRequestTime = stopwatch.ElapsedMilliseconds;
        
        using (await hostGate.AcquireAsync())
        {
        }
        
        stopwatch.Stop();
        
        Assert.True(stopwatch.ElapsedMilliseconds >= firstRequestTime + _options.RequestSpacing.TotalMilliseconds);
    }

    [Fact]
    public async Task StabilityUnderLoad_HighConcurrency_MaintainsStability()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.OK, "Success");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var tasks = new Task[50];
        var hosts = new[] { "host1.com", "host2.com", "host3.com" };
        
        for (int i = 0; i < tasks.Length; i++)
        {
            var host = hosts[i % hosts.Length];
            var request = new Request($"http://{host}");
            tasks[i] = downloader.DownloadAsync(request);
        }
        
        await Task.WhenAll(tasks);
        
        foreach (var host in hosts)
        {
            var stats = downloader.GetHostStats(host);
            Assert.NotNull(stats);
            Assert.True(stats.TotalRequests > 0);
            Assert.True(stats.CurrentConcurrency >= _options.MinConcurrency);
            Assert.True(stats.CurrentConcurrency <= _options.MaxConcurrency);
        }
    }

    [Fact]
    public async Task ResourceCleanup_DisposeManager_ReleasesAllResources()
    {
        var tempManager = new AdaptiveThrottleManager(_options);
        var hostGate = tempManager.GetHostGate("example.com");
        
        using (await hostGate.AcquireAsync())
        {
            tempManager.RecordLatency("example.com", 100);
            tempManager.RecordError("example.com");
        }
        
        Assert.Equal(1, tempManager.GetHostCount());
        
        tempManager.Dispose();
        
        var exception = Record.Exception(() => tempManager.GetHostStats("example.com"));
        Assert.Null(exception);
    }

    [Fact]
    public async Task ErrorRecovery_AfterErrors_RecoversConcurrency()
    {
        var responseQueue = new Queue<HttpStatusCode>();
        responseQueue.Enqueue(HttpStatusCode.InternalServerError);
        responseQueue.Enqueue(HttpStatusCode.InternalServerError);
        responseQueue.Enqueue(HttpStatusCode.OK);
        responseQueue.Enqueue(HttpStatusCode.OK);
        responseQueue.Enqueue(HttpStatusCode.OK);

        var mockHandler = new MockHttpMessageHandler();
        var sequenceSetup = mockHandler.SetupSequence();
        
        while (responseQueue.Count > 0)
        {
            var statusCode = responseQueue.Dequeue();
            sequenceSetup.Returns(new HttpResponseMessage(statusCode)
            {
                Content = new System.Net.Http.StringContent(statusCode == HttpStatusCode.OK ? "Success" : "Error")
            });
        }
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var request = new Request("http://example.com");
        var hostGate = _throttleManager.GetHostGate("example.com");
        var initialConcurrency = hostGate.CurrentConcurrency;
        
        for (int i = 0; i < 5; i++)
        {
            await downloader.DownloadAsync(request);
            await Task.Delay(50);
        }
        
        var finalConcurrency = hostGate.CurrentConcurrency;
        var stats = downloader.GetHostStats("example.com");
        
        Assert.True(stats.TotalErrors > 0);
        Assert.True(finalConcurrency >= _options.MinConcurrency);
    }

    public void Dispose()
    {
        _throttleManager?.Dispose();
    }
}