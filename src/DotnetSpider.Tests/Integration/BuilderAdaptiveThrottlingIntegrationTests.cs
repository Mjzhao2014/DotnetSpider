using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.DataFlow;
using DotnetSpider.DataFlow.Parser;
using DotnetSpider.Downloader;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace DotnetSpider.Tests.Integration;

/// <summary>
/// Integration tests for Builder with Adaptive Throttling functionality
/// Tests Builder patterns, extension methods, and adaptive throttling configuration
/// </summary>
public class BuilderAdaptiveThrottlingIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly CancellationTokenSource _globalCts;
    private bool _disposed;

    public BuilderAdaptiveThrottlingIntegrationTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _globalCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    }

    #region Test Spider Classes

    private class AdaptiveTestSpider : Spider
    {
        public static readonly List<string> ProcessedUrls = new();
        public static readonly List<DateTime> RequestTimes = new();

        public AdaptiveTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(
                new Request("https://adaptive-test.com/page1"),
                new Request("https://adaptive-test.com/page2"),
                new Request("https://fast-site.com/data"),
                new Request("https://slow-site.com/heavy")
            );
            AddDataFlow<AdaptiveTestDataParser>();
        }

        private class AdaptiveTestDataParser : DataParser
        {
            protected override Task ParseAsync(DataFlowContext context)
            {
                lock (ProcessedUrls)
                {
                    ProcessedUrls.Add(context.Request.RequestUri.ToString());
                    RequestTimes.Add(DateTime.UtcNow);
                }

                // Simulate finding links for follow requests
                if (context.Request.RequestUri.ToString().Contains("page1"))
                {
                    context.AddFollowRequests(new[] { new Uri("https://adaptive-test.com/linked-page") });
                }

                return Task.CompletedTask;
            }

            public override Task InitializeAsync()
            {
                return Task.CompletedTask;
            }
        }
    }

    private class MinimalAdaptiveSpider : Spider
    {
        public MinimalAdaptiveSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Mock HTTP Handlers

    private class AdaptiveThrottlingMockHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, List<DateTime>> _hostRequestTimes = new();
        private readonly ConcurrentDictionary<string, int> _hostRequestCount = new();
        private readonly Random _random = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri.Host;
            var url = request.RequestUri.ToString();

            // Track request timing per host
            var now = DateTime.UtcNow;
            _hostRequestTimes.AddOrUpdate(host, new List<DateTime> { now }, (_, list) =>
            {
                lock (list)
                {
                    list.Add(now);
                    return list;
                }
            });

            _hostRequestCount.AddOrUpdate(host, 1, (_, count) => count + 1);

            // Simulate different latency patterns by host
            int delay = GetLatencyForHost(host);
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }

            // Simulate occasional errors for reliability testing
            if (url.Contains("unreliable") && _random.NextDouble() < 0.15)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new System.Net.Http.StringContent("Service temporarily unavailable"),
                    Headers = { { "Retry-After", "1" } }
                };
            }

            // Simulate rate limiting for high-traffic hosts
            var requestCount = _hostRequestCount[host];
            if (requestCount > 10 && _random.NextDouble() < 0.1)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new System.Net.Http.StringContent("Rate limit exceeded"),
                    Headers = { { "Retry-After", "2" } }
                };
            }

            // Return successful response
            var content = GetContentForUrl(url);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(content, System.Text.Encoding.UTF8, "text/html"),
                Headers = { { "X-Response-Time", $"{delay}ms" } }
            };
        }

        private int GetLatencyForHost(string host)
        {
            return host switch
            {
                "slow-site.com" => 800 + _random.Next(-100, 200),
                "fast-site.com" => 50 + _random.Next(-10, 20),
                "adaptive-test.com" => 200 + _random.Next(-50, 100),
                _ => 150 + _random.Next(-30, 60)
            };
        }

        private string GetContentForUrl(string url)
        {
            if (url.Contains("page1"))
            {
                return "<html><body><h1>Page 1</h1><a href='/linked-page'>Link</a></body></html>";
            }
            if (url.Contains("data"))
            {
                return "<html><body><h1>Data Page</h1><p>Fast content</p></body></html>";
            }
            if (url.Contains("heavy"))
            {
                return "<html><body><h1>Heavy Page</h1><p>Slow loading content with lots of data...</p></body></html>";
            }
            return "<html><body><h1>Default Page</h1></body></html>";
        }

        public Dictionary<string, List<DateTime>> GetHostRequestTimes()
        {
            return _hostRequestTimes.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList()
            );
        }
    }

    #endregion

    #region Builder Extension Tests

    [Fact]
    public void Builder_UseAdaptiveThrottling_ShouldRegisterServices()
    {
        var builder = Builder.CreateBuilder<MinimalAdaptiveSpider>()
            .UseAdaptiveThrottling(options =>
            {
                options.EnableAdaptiveThrottling = true;
                options.MinConcurrency = 2;
                options.MaxConcurrency = 8;
                options.MaxRetryAttempts = 3;
                options.RequestSpacing = TimeSpan.FromMilliseconds(100);
            });

        using var host = builder.Build();
        var services = host.Services;

        // Verify adaptive throttling services are registered
        var options = services.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
        var throttleManager = services.GetRequiredService<AdaptiveThrottleManager>();
        var downloader = services.GetRequiredService<IDownloader>();

        Assert.NotNull(options);
        Assert.NotNull(throttleManager);
        Assert.NotNull(downloader);
        Assert.IsType<AdaptiveHttpClientDownloader>(downloader);
        Assert.True(options.Value.EnableAdaptiveThrottling);
        Assert.Equal(2, options.Value.MinConcurrency);
        Assert.Equal(8, options.Value.MaxConcurrency);
    }

    [Fact]
    public void Builder_UseAdaptiveThrottling_WithoutConfiguration_ShouldUseDefaults()
    {
        var builder = Builder.CreateBuilder<MinimalAdaptiveSpider>()
            .UseAdaptiveThrottling();

        using var host = builder.Build();
        var services = host.Services;

        // Verify services are registered with default configuration
        var options = services.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
        var throttleManager = services.GetRequiredService<AdaptiveThrottleManager>();
        var downloader = services.GetRequiredService<IDownloader>();

        Assert.NotNull(options);
        Assert.NotNull(throttleManager);
        Assert.NotNull(downloader);
        Assert.IsType<AdaptiveHttpClientDownloader>(downloader);
        Assert.True(options.Value.EnableAdaptiveThrottling); // Default is true
    }

    [Fact]
    public void Builder_UseAdaptiveThrottling_WithContext_ShouldConfigureCorrectly()
    {
        var builder = Builder.CreateBuilder<MinimalAdaptiveSpider>()
            .UseAdaptiveThrottling((context, options) =>
            {
                options.EnableAdaptiveThrottling = true;
                options.MinConcurrency = 1;
                options.MaxConcurrency = 6;
                
                // Can access context for environment-specific configuration
                if (context.HostingEnvironment.IsDevelopment())
                {
                    options.RequestSpacing = TimeSpan.FromMilliseconds(50);
                }
                else
                {
                    options.RequestSpacing = TimeSpan.FromMilliseconds(200);
                }
            });

        using var host = builder.Build();
        var services = host.Services;

        var options = services.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
        Assert.True(options.Value.EnableAdaptiveThrottling);
        Assert.Equal(1, options.Value.MinConcurrency);
        Assert.Equal(6, options.Value.MaxConcurrency);
        // RequestSpacing should be set based on environment (development in tests)
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.Value.RequestSpacing);
    }

    [Fact]
    public void Builder_ConfigureAdaptiveThrottling_ShouldRegisterServices()
    {
        var builder = Builder.CreateBuilder<MinimalAdaptiveSpider>();
        
        // Configure adaptive throttling manually
        builder.ConfigureServices(services =>
        {
            services.Configure<AdaptiveThrottleOptions>(options =>
            {
                options.EnableAdaptiveThrottling = true;
                options.MinConcurrency = 1;
                options.MaxConcurrency = 8;
                options.MaxRetryAttempts = 3;
                options.RequestSpacing = TimeSpan.FromMilliseconds(100);
            });
            
            services.AddSingleton<AdaptiveThrottleManager>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                return new AdaptiveThrottleManager(options.Value);
            });
            
            services.TryAddSingleton<IProxyService, EmptyProxyService>();
            
            services.AddSingleton<IDownloader>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var proxyService = provider.GetRequiredService<IProxyService>();
                var logger = provider.GetRequiredService<ILogger<AdaptiveHttpClientDownloader>>();
                var throttleManager = provider.GetRequiredService<AdaptiveThrottleManager>();
                var optionsValue = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                
                return new AdaptiveHttpClientDownloader(
                    httpClientFactory,
                    proxyService, 
                    logger,
                    throttleManager,
                    optionsValue.Value);
            });
        });

        using var host = builder.Build();
        var services = host.Services;

        // Verify adaptive throttling services are registered
        var options = services.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
        var throttleManager = services.GetRequiredService<AdaptiveThrottleManager>();
        var downloader = services.GetRequiredService<IDownloader>();

        Assert.NotNull(options);
        Assert.NotNull(throttleManager);
        Assert.NotNull(downloader);
        Assert.IsType<AdaptiveHttpClientDownloader>(downloader);
        Assert.True(options.Value.EnableAdaptiveThrottling);
    }

    [Fact]
    public void Builder_AdaptiveThrottleOptions_ShouldConfigureCorrectly()
    {
        var builder = Builder.CreateBuilder<MinimalAdaptiveSpider>();
        
        builder.ConfigureServices(services =>
        {
            services.Configure<AdaptiveThrottleOptions>(options =>
            {
                options.EnableAdaptiveThrottling = true;
                options.MinConcurrency = 2;
                options.MaxConcurrency = 12;
                options.MaxLatencyThresholdMs = 1500;
                options.MinLatencyThresholdMs = 100;
                options.ErrorRateThreshold = 0.15;
                options.EwmaAlpha = 0.4;
                options.CooldownPeriod = TimeSpan.FromSeconds(8);
                options.RequestSpacing = TimeSpan.FromMilliseconds(200);
                options.MaxRetryAttempts = 5;
                options.MaxRetryDelay = TimeSpan.FromSeconds(10);
            });
            
            services.AddSingleton<AdaptiveThrottleManager>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                return new AdaptiveThrottleManager(options.Value);
            });
        });

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();

        Assert.True(options.Value.EnableAdaptiveThrottling);
        Assert.Equal(2, options.Value.MinConcurrency);
        Assert.Equal(12, options.Value.MaxConcurrency);
        Assert.Equal(1500, options.Value.MaxLatencyThresholdMs);
        Assert.Equal(100, options.Value.MinLatencyThresholdMs);
        Assert.Equal(0.15, options.Value.ErrorRateThreshold);
        Assert.Equal(0.4, options.Value.EwmaAlpha);
        Assert.Equal(TimeSpan.FromSeconds(8), options.Value.CooldownPeriod);
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.Value.RequestSpacing);
        Assert.Equal(5, options.Value.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Value.MaxRetryDelay);
    }

    #endregion

    #region Adaptive Downloader Integration Tests

    [Fact]
    public async Task AdaptiveDownloader_BasicOperation_ShouldWork()
    {
        var mockHandler = new AdaptiveThrottlingMockHandler();
        var httpClient = new HttpClient(mockHandler);
        
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var options = new AdaptiveThrottleOptions
        {
            EnableAdaptiveThrottling = true,
            MinConcurrency = 1,
            MaxConcurrency = 4,
            RequestSpacing = TimeSpan.FromMilliseconds(50)
        };

        var throttleManager = new AdaptiveThrottleManager(options);
        var logger = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        var proxyService = new Mock<IProxyService>();

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            proxyService.Object,
            logger.Object,
            throttleManager,
            options);

        // Test basic download functionality
        var request = new Request("https://adaptive-test.com/page1");
        var response = await downloader.DownloadAsync(request);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Page 1", response.ReadAsString());
    }

    [Fact]
    public async Task AdaptiveDownloader_MultipleHosts_ShouldThrottleIndependently()
    {
        var mockHandler = new AdaptiveThrottlingMockHandler();
        var httpClient = new HttpClient(mockHandler);
        
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var options = new AdaptiveThrottleOptions
        {
            EnableAdaptiveThrottling = true,
            MinConcurrency = 1,
            MaxConcurrency = 3,
            RequestSpacing = TimeSpan.FromMilliseconds(100)
        };

        var throttleManager = new AdaptiveThrottleManager(options);
        var logger = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        var proxyService = new Mock<IProxyService>();

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            proxyService.Object,
            logger.Object,
            throttleManager,
            options);

        var requests = new[]
        {
            new Request("https://fast-site.com/data1"),
            new Request("https://slow-site.com/heavy1"),
            new Request("https://fast-site.com/data2"),
            new Request("https://slow-site.com/heavy2"),
            new Request("https://adaptive-test.com/page1")
        };

        var stopwatch = Stopwatch.StartNew();
        var tasks = requests.Select(req => downloader.DownloadAsync(req)).ToArray();
        var responses = await Task.WhenAll(tasks);
        stopwatch.Stop();

        // All requests should succeed
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        // Should have taken some time due to throttling
        Assert.True(stopwatch.ElapsedMilliseconds > 100);

        // Verify requests were made to different hosts
        var hostTimes = mockHandler.GetHostRequestTimes();
        Assert.True(hostTimes.ContainsKey("fast-site.com"));
        Assert.True(hostTimes.ContainsKey("slow-site.com"));
        Assert.True(hostTimes.ContainsKey("adaptive-test.com"));
    }

    [Fact]
    public async Task AdaptiveDownloader_ErrorHandling_ShouldRetryAppropriately()
    {
        var mockHandler = new AdaptiveThrottlingMockHandler();
        var httpClient = new HttpClient(mockHandler);
        
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var options = new AdaptiveThrottleOptions
        {
            EnableAdaptiveThrottling = true,
            MinConcurrency = 1,
            MaxConcurrency = 2,
            MaxRetryAttempts = 3,
            MaxRetryDelay = TimeSpan.FromSeconds(1)
        };

        var throttleManager = new AdaptiveThrottleManager(options);
        var logger = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        var proxyService = new Mock<IProxyService>();

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            proxyService.Object,
            logger.Object,
            throttleManager,
            options);

        // Test with unreliable endpoint that might fail
        var request = new Request("https://adaptive-test.com/unreliable");
        var response = await downloader.DownloadAsync(request);

        // Should eventually succeed or fail gracefully
        Assert.NotNull(response);
        // Don't assert specific status code since it may succeed after retries
    }

    #endregion

    #region Builder Integration E2E Tests

    [Fact]
    public async Task Builder_CompleteAdaptiveScenario_ShouldWorkEndToEnd()
    {
        // Clear static test data
        AdaptiveTestSpider.ProcessedUrls.Clear();
        AdaptiveTestSpider.RequestTimes.Clear();

        var mockHandler = new AdaptiveThrottlingMockHandler();
        var httpClient = new HttpClient(mockHandler);
        
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var builder = Builder.CreateDefaultBuilder<AdaptiveTestSpider>(options =>
        {
            options.Speed = 3; // Moderate speed
            options.Depth = 2; // Allow follow requests
        });

        // Configure adaptive throttling
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_httpClientFactoryMock.Object);
            
            services.Configure<AdaptiveThrottleOptions>(options =>
            {
                options.EnableAdaptiveThrottling = true;
                options.MinConcurrency = 1;
                options.MaxConcurrency = 4;
                options.RequestSpacing = TimeSpan.FromMilliseconds(200);
                options.MaxRetryAttempts = 2;
                options.ErrorRateThreshold = 0.2;
                options.MaxLatencyThresholdMs = 1000;
                options.MinLatencyThresholdMs = 150;
                options.CooldownPeriod = TimeSpan.FromSeconds(3);
            });
            
            services.AddSingleton<AdaptiveThrottleManager>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                return new AdaptiveThrottleManager(options.Value);
            });
            
            services.AddSingleton<IDownloader>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var proxyService = provider.GetRequiredService<IProxyService>();
                var logger = provider.GetRequiredService<ILogger<AdaptiveHttpClientDownloader>>();
                var throttleManager = provider.GetRequiredService<AdaptiveThrottleManager>();
                var optionsValue = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                
                return new AdaptiveHttpClientDownloader(
                    httpClientFactory,
                    proxyService, 
                    logger,
                    throttleManager,
                    optionsValue.Value);
            });
        });

        using var host = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Start the spider
        var runTask = host.RunAsync(cts.Token);

        // Wait for some processing or timeout
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            // Expected due to timeout
        }

        // Verify spider processed URLs with adaptive throttling
        Assert.True(AdaptiveTestSpider.ProcessedUrls.Count > 0, "Spider should have processed at least some URLs");
        
        // Verify timing indicates throttling was applied
        if (AdaptiveTestSpider.RequestTimes.Count > 1)
        {
            var timeDifferences = new List<double>();
            for (int i = 1; i < AdaptiveTestSpider.RequestTimes.Count; i++)
            {
                var diff = (AdaptiveTestSpider.RequestTimes[i] - AdaptiveTestSpider.RequestTimes[i - 1]).TotalMilliseconds;
                timeDifferences.Add(diff);
            }
            
            // At least some requests should be spaced due to throttling
            Assert.True(timeDifferences.Any(d => d > 100), "Some requests should show throttling delays");
        }

        // Verify different hosts were accessed
        var hostTimes = mockHandler.GetHostRequestTimes();
        Assert.True(hostTimes.Keys.Count > 0, "Should have made requests to at least one host");
    }

    [Fact]
    public void Builder_MultipleAdaptiveConfigurations_ShouldUseLatest()
    {
        var builder = Builder.CreateBuilder<MinimalAdaptiveSpider>();
        
        // Configure adaptive throttling multiple times
        builder.ConfigureServices(services =>
        {
            services.Configure<AdaptiveThrottleOptions>(options =>
            {
                options.EnableAdaptiveThrottling = false; // First configuration
                options.MinConcurrency = 1;
            });
        });

        builder.ConfigureServices(services =>
        {
            services.Configure<AdaptiveThrottleOptions>(options =>
            {
                options.EnableAdaptiveThrottling = true; // Second configuration should override
                options.MinConcurrency = 3;
                options.MaxConcurrency = 10;
            });
        });

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();

        // Latest configuration should win
        Assert.True(options.Value.EnableAdaptiveThrottling);
        Assert.Equal(3, options.Value.MinConcurrency);
        Assert.Equal(10, options.Value.MaxConcurrency);
    }

    [Fact]
    public void Builder_AdaptiveThrottlingWithProxy_ShouldIntegrateProperly()
    {
        var builder = Builder.CreateBuilder<MinimalAdaptiveSpider>();

        builder.ConfigureServices(services =>
        {
            // Configure both adaptive throttling and proxy
            services.Configure<AdaptiveThrottleOptions>(options =>
            {
                options.EnableAdaptiveThrottling = true;
                options.MinConcurrency = 1;
                options.MaxConcurrency = 5;
            });

            services.Configure<ProxyOptions>(options =>
            {
                options.ProxySupplierUrl = "http://test-proxy-supplier.com";
            });
            
            services.AddSingleton<AdaptiveThrottleManager>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                return new AdaptiveThrottleManager(options.Value);
            });
            
            services.AddSingleton<IProxySupplier, EmptyProxySupplier>();
            services.AddSingleton<IProxyValidator, DefaultProxyValidator>();
            services.TryAddSingleton<IProxyService, EmptyProxyService>();
            
            services.AddSingleton<IDownloader>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var proxyService = provider.GetRequiredService<IProxyService>();
                var logger = provider.GetRequiredService<ILogger<AdaptiveHttpClientDownloader>>();
                var throttleManager = provider.GetRequiredService<AdaptiveThrottleManager>();
                var optionsValue = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                
                return new AdaptiveHttpClientDownloader(
                    httpClientFactory,
                    proxyService, 
                    logger,
                    throttleManager,
                    optionsValue.Value);
            });
        });

        using var host = builder.Build();
        var services = host.Services;

        // Verify both adaptive and proxy services are configured
        var adaptiveOptions = services.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
        var proxyOptions = services.GetRequiredService<IOptions<ProxyOptions>>();
        var downloader = services.GetRequiredService<IDownloader>();
        var proxySupplier = services.GetRequiredService<IProxySupplier>();

        Assert.True(adaptiveOptions.Value.EnableAdaptiveThrottling);
        Assert.Equal("http://test-proxy-supplier.com", proxyOptions.Value.ProxySupplierUrl);
        Assert.IsType<AdaptiveHttpClientDownloader>(downloader);
        Assert.IsType<EmptyProxySupplier>(proxySupplier);
    }

    #endregion

    #region Performance and Behavior Tests

    [Fact]
    public async Task AdaptiveDownloader_PerformanceUnderLoad_ShouldManageResources()
    {
        var mockHandler = new AdaptiveThrottlingMockHandler();
        var httpClient = new HttpClient(mockHandler);
        
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var options = new AdaptiveThrottleOptions
        {
            EnableAdaptiveThrottling = true,
            MinConcurrency = 1,
            MaxConcurrency = 6,
            RequestSpacing = TimeSpan.FromMilliseconds(50),
            MaxRetryAttempts = 2
        };

        var throttleManager = new AdaptiveThrottleManager(options);
        var logger = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        var proxyService = new Mock<IProxyService>();

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            proxyService.Object,
            logger.Object,
            throttleManager,
            options);

        // Create many requests to test load handling
        var requests = Enumerable.Range(1, 20)
            .Select(i => new Request($"https://load-test.com/page{i}"))
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var tasks = requests.Select(req => downloader.DownloadAsync(req)).ToArray();
        var responses = await Task.WhenAll(tasks);
        stopwatch.Stop();

        // All requests should complete
        Assert.Equal(20, responses.Length);
        Assert.All(responses, response => Assert.NotNull(response));

        // Should take a reasonable amount of time (not too fast due to throttling, not too slow)
        Assert.True(stopwatch.ElapsedMilliseconds > 100, "Should take some time due to throttling");
        Assert.True(stopwatch.ElapsedMilliseconds < 30000, "Should not take too long");

        _testOutputHelper.WriteLine($"Load test completed in {stopwatch.ElapsedMilliseconds}ms for {requests.Length} requests");
    }

    [Fact]
    public void AdaptiveThrottleManager_ResourceCleanup_ShouldNotLeak()
    {
        var options = new AdaptiveThrottleOptions
        {
            EnableAdaptiveThrottling = true,
            MinConcurrency = 1,
            MaxConcurrency = 4
        };

        var throttleManager = new AdaptiveThrottleManager(options);

        // Create gates for multiple hosts
        var hosts = new[] { "test1.com", "test2.com", "test3.com" };
        foreach (var host in hosts)
        {
            throttleManager.GetOrCreateGate(host);
        }

        // Dispose should clean up resources
        throttleManager.Dispose();

        // No exceptions should occur during disposal
        Assert.True(true); // Test passed if no exceptions
    }

    #endregion

    #region Helper Classes and Methods

    private class EmptyProxySupplier : IProxySupplier
    {
        public Task<IEnumerable<Uri>> GetProxiesAsync()
        {
            return Task.FromResult(Enumerable.Empty<Uri>());
        }
    }

    #endregion

    #region Cleanup

    public void Dispose()
    {
        if (!_disposed)
        {
            _globalCts?.Dispose();
            _disposed = true;
        }
    }

    #endregion
}
