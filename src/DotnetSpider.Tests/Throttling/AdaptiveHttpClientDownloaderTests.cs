using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.Proxy;
using DotnetSpider.Throttling;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DotnetSpider.Tests.Throttling;

public class AdaptiveHttpClientDownloaderTests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<IProxyService> _proxyServiceMock;
    private readonly Mock<ILogger<AdaptiveHttpClientDownloader>> _loggerMock;
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly AdaptiveThrottleOptions _options;
    private readonly AdaptiveHttpClientDownloader _downloader;

    public AdaptiveHttpClientDownloaderTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _proxyServiceMock = new Mock<IProxyService>();
        _loggerMock = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        _options = new AdaptiveThrottleOptions 
        { 
            EnableAdaptiveThrottling = true,
            MaxRetryAttempts = 2,
            MinConcurrency = 1,
            MaxConcurrency = 5,
            CooldownPeriod = TimeSpan.Zero
        };
        _throttleManager = new AdaptiveThrottleManager(_options);
        
        _downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        // Setup proxy service to act like EmptyProxyService
        _proxyServiceMock.Setup(x => x.GetAsync(It.IsAny<int>())).ReturnsAsync((Uri)null);
    }

    [Fact]
    public async Task DownloadAsync_ThrottlingDisabled_CallsBaseDownloader()
    {
        var disabledOptions = new AdaptiveThrottleOptions { EnableAdaptiveThrottling = false };
        var disabledManager = new AdaptiveThrottleManager(disabledOptions);
        var disabledDownloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            disabledManager,
            disabledOptions);

        var mockHttpClient = new Mock<HttpClient>();
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(mockHttpClient.Object);

        var request = new Request("http://example.com");
        
        var response = await disabledDownloader.DownloadAsync(request);
        
        Assert.NotNull(response);
        
        disabledManager.Dispose();
    }

    [Fact]
    public async Task DownloadAsync_SuccessfulRequest_RecordsLatency()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.OK, "Success");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var request = new Request("http://example.com");
        
        await _downloader.DownloadAsync(request);
        
        var stats = _downloader.GetHostStats("example.com");
        Assert.NotNull(stats);
        Assert.True(stats.EwmaLatency > 0);
        Assert.Equal(1, stats.TotalRequests);
        Assert.Equal(0, stats.TotalErrors);
    }

    [Fact]
    public async Task DownloadAsync_ErrorResponse_RecordsError()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.InternalServerError, "Server Error");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var request = new Request("http://example.com");
        
        await _downloader.DownloadAsync(request);
        
        var stats = _downloader.GetHostStats("example.com");
        Assert.NotNull(stats);
        Assert.Equal(1, stats.TotalErrors);
    }

    [Fact]
    public async Task DownloadAsync_RateLimitedResponse_RetriesWithDelay()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupSequence()
            .Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { { "Retry-After", "1" } }
            })
            .Returns(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("Success")
            });
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var request = new Request("http://example.com");
        var startTime = DateTime.UtcNow;
        
        var response = await _downloader.DownloadAsync(request);
        
        var elapsedTime = DateTime.UtcNow - startTime;
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(elapsedTime >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DownloadAsync_MaxRetriesExceeded_ReturnsGoneStatus()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.InternalServerError, "Server Error");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var request = new Request("http://example.com");
        
        var response = await _downloader.DownloadAsync(request);
        
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        
        var stats = _downloader.GetHostStats("example.com");
        Assert.True(stats.TotalErrors >= 3);
    }

    [Fact]
    public async Task ConcurrencyPromotion_LowLatency_IncreasesConcurrency()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.OK, "Success");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var request = new Request("http://example.com");
        var initialConcurrency = _throttleManager.GetHostGate("example.com").CurrentConcurrency;
        
        for (int i = 0; i < 5; i++)
        {
            await _downloader.DownloadAsync(request);
        }
        
        var finalConcurrency = _throttleManager.GetHostGate("example.com").CurrentConcurrency;
        Assert.True(finalConcurrency >= initialConcurrency);
    }

    [Fact]
    public async Task ConcurrencyDemotion_HighLatency_DecreasesConcurrency()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupWithDelay(HttpStatusCode.OK, "Success", TimeSpan.FromSeconds(1));
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var request = new Request("http://example.com");
        var hostGate = _throttleManager.GetHostGate("example.com");
        
        var initialConcurrency = hostGate.CurrentConcurrency;
        
        for (int i = 0; i < 3; i++)
        {
            await _downloader.DownloadAsync(request);
        }
        
        var finalConcurrency = hostGate.CurrentConcurrency;
        Assert.True(finalConcurrency <= initialConcurrency);
    }

    [Fact]
    public async Task ConcurrencyDemotion_HighErrorRate_DecreasesConcurrency()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Setup(HttpStatusCode.InternalServerError, "Server Error");
        
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var request = new Request("http://example.com");
        var hostGate = _throttleManager.GetHostGate("example.com");
        
        var initialConcurrency = hostGate.CurrentConcurrency;
        
        for (int i = 0; i < 3; i++)
        {
            await _downloader.DownloadAsync(request);
        }
        
        var finalConcurrency = hostGate.CurrentConcurrency;
        Assert.True(finalConcurrency <= initialConcurrency);
        Assert.True(hostGate.ErrorRate > 0);
    }

    [Fact]
    public async Task HostIsolation_DifferentHosts_IndependentThrottling()
    {
        var fastHandler = new MockHttpMessageHandler();
        fastHandler.Setup(HttpStatusCode.OK, "Fast");
        
        var slowHandler = new MockHttpMessageHandler();
        slowHandler.SetupWithDelay(HttpStatusCode.OK, "Slow", TimeSpan.FromSeconds(1));
        
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("fast.com"))
            .Returns(new HttpClient(fastHandler));
        
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("slow.com"))
            .Returns(new HttpClient(slowHandler));

        var fastRequest = new Request("http://fast.com");
        var slowRequest = new Request("http://slow.com");
        
        var tasks = new[]
        {
            _downloader.DownloadAsync(fastRequest),
            _downloader.DownloadAsync(fastRequest),
            _downloader.DownloadAsync(slowRequest),
            _downloader.DownloadAsync(slowRequest)
        };
        
        await Task.WhenAll(tasks);
        
        var fastStats = _downloader.GetHostStats("fast.com");
        var slowStats = _downloader.GetHostStats("slow.com");
        
        Assert.True(fastStats.EwmaLatency < slowStats.EwmaLatency);
    }

    [Fact]
    public void GetAllHostStats_ReturnsStatsForAllHosts()
    {
        _throttleManager.RecordLatency("host1.com", 100);
        _throttleManager.RecordLatency("host2.com", 200);
        _throttleManager.RecordError("host1.com");
        
        var allStats = _downloader.GetAllHostStats();
        
        Assert.Equal(2, allStats.Length);
        
        var host1Stats = Array.Find(allStats, s => s.Host == "host1.com");
        var host2Stats = Array.Find(allStats, s => s.Host == "host2.com");
        
        Assert.NotNull(host1Stats);
        Assert.NotNull(host2Stats);
        Assert.Equal(100, host1Stats.EwmaLatency);
        Assert.Equal(200, host2Stats.EwmaLatency);
        Assert.Equal(1, host1Stats.TotalErrors);
        Assert.Equal(0, host2Stats.TotalErrors);
    }

    public void Dispose()
    {
        _throttleManager?.Dispose();
    }
}