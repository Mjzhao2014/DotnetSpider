using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Downloader;
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
    private readonly Mock<IHostThrottler> _hostThrottlerMock;
    private readonly AdaptiveDownloaderOptions _options;
    private readonly AdaptiveHttpClientDownloader _downloader;
    private readonly HttpClient _httpClient;

    public AdaptiveHttpClientDownloaderTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _proxyServiceMock = new Mock<IProxyService>();
        _loggerMock = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        _hostThrottlerMock = new Mock<IHostThrottler>();
        _options = new AdaptiveDownloaderOptions();
        
        _httpClient = new HttpClient(new TestMessageHandler());
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(_httpClient);
        
        _proxyServiceMock.Setup(p => p.GetAsync(It.IsAny<int>()))
            .Returns(Task.FromResult<Uri>(null));
        
        _downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _hostThrottlerMock.Object,
            _options);
    }

    [Fact]
    public void Constructor_ThrowsOnNullHostThrottler()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new AdaptiveHttpClientDownloader(
                _httpClientFactoryMock.Object,
                _proxyServiceMock.Object,
                _loggerMock.Object,
                null));
    }

    [Fact]
    public async Task DownloadAsync_AcquiresAndReleasesThrottleToken()
    {
        var request = new Request("https://example.com/test");
        var mockToken = new Mock<IDisposable>();
        
        _hostThrottlerMock.Setup(t => t.AcquireAsync("example.com", default))
            .Returns(Task.FromResult(mockToken.Object));
        
        var response = await _downloader.DownloadAsync(request);
        
        _hostThrottlerMock.Verify(t => t.AcquireAsync("example.com", default), Times.Once);
        mockToken.Verify(t => t.Dispose(), Times.Once);
        
        Assert.NotNull(response);
    }

    [Fact]
    public async Task DownloadAsync_RecordsSuccessOnSuccessfulResponse()
    {
        var request = new Request("https://example.com/test");
        var mockToken = new Mock<IDisposable>();
        
        _hostThrottlerMock.Setup(t => t.AcquireAsync("example.com", default))
            .Returns(Task.FromResult(mockToken.Object));
        
        var response = await _downloader.DownloadAsync(request);
        
        _hostThrottlerMock.Verify(t => t.RecordSuccess("example.com", It.IsAny<TimeSpan>()), Times.Once);
        _hostThrottlerMock.Verify(t => t.RecordError("example.com", It.IsAny<TimeSpan>()), Times.Never);
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DownloadAsync_RecordsErrorOnFailureResponse()
    {
        var request = new Request("https://example.com/error");
        var mockToken = new Mock<IDisposable>();
        
        _hostThrottlerMock.Setup(t => t.AcquireAsync("example.com", default))
            .Returns(Task.FromResult(mockToken.Object));
        
        var response = await _downloader.DownloadAsync(request);
        
        _hostThrottlerMock.Verify(t => t.RecordError("example.com", It.IsAny<TimeSpan>()), Times.Once);
        _hostThrottlerMock.Verify(t => t.RecordSuccess("example.com", It.IsAny<TimeSpan>()), Times.Never);
        
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DownloadAsync_RecordsErrorOnException()
    {
        var request = new Request("https://example.com/exception");
        var mockToken = new Mock<IDisposable>();
        
        _hostThrottlerMock.Setup(t => t.AcquireAsync("example.com", default))
            .Returns(Task.FromResult(mockToken.Object));
        
        var response = await _downloader.DownloadAsync(request);
        
        _hostThrottlerMock.Verify(t => t.RecordError("example.com", It.IsAny<TimeSpan>()), Times.Once);
        
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public void GetHostMetrics_DelegatesToHostThrottler()
    {
        var expectedMetrics = new HostMetrics { CurrentConcurrency = 3, AverageLatency = 150.5 };
        
        _hostThrottlerMock.Setup(t => t.GetMetrics("example.com"))
            .Returns(expectedMetrics);
        
        var metrics = _downloader.GetHostMetrics("example.com");
        
        Assert.Equal(expectedMetrics.CurrentConcurrency, metrics.CurrentConcurrency);
        Assert.Equal(expectedMetrics.AverageLatency, metrics.AverageLatency);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _downloader?.Dispose();
    }
}

public class TestMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        var response = request.RequestUri.ToString() switch
        {
            string url when url.Contains("/error") => new HttpResponseMessage(HttpStatusCode.NotFound),
            string url when url.Contains("/exception") => throw new HttpRequestException("Test exception"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("Test response")
            }
        };
        
        return Task.FromResult(response);
    }
}