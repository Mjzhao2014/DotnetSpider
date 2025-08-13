using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Downloader;
using DotnetSpider.Http;
using DotnetSpider.Proxy;
using DotnetSpider.Robots;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace DotnetSpider.Tests;

/// <summary>
/// End-to-end tests that verify the complete robots.txt compliance flow
/// </summary>
public class RobotsTxtE2ETests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _httpMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<IHttpClientFactory> _httpFactoryMock;
    private readonly RobotsAwareHttpClientDownloader _downloader;

    public RobotsTxtE2ETests()
    {
        _httpMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMock.Object);
        
        _httpFactoryMock = new Mock<IHttpClientFactory>();
        _httpFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(_httpClient);

        var robotsTxtManager = new RobotsTxtManager(_httpFactoryMock.Object, NullLogger<RobotsTxtManager>.Instance);
        var crawlDelayManager = new CrawlDelayManager(NullLogger<CrawlDelayManager>.Instance);

        _downloader = new RobotsAwareHttpClientDownloader(
            _httpFactoryMock.Object,
            new EmptyProxyService(),
            NullLogger<RobotsAwareHttpClientDownloader>.Instance,
            robotsTxtManager,
            crawlDelayManager,
            NullLogger<HttpClientDownloader>.Instance
        );
    }

    [Fact]
    public async Task E2E_CompleteRobotsFlow_BlocksDisallowedPath()
    {
        // Arrange: Setup a typical robots.txt that blocks admin pages
        var robotsContent = @"
User-agent: *
Disallow: /admin/
Disallow: /*.pdf$
Crawl-delay: 0.5";

        SetupRobotsResponse("example.com", robotsContent);

        // Act: Try to access a blocked admin page
        var request = new Request("https://example.com/admin/secret.html");
        var response = await _downloader.DownloadAsync(request);

        // Assert: Request should be blocked
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Blocked by robots.txt", response.ReasonPhrase);
        
        // Verify robots.txt was fetched
        VerifyRobotsFetched("example.com");
    }

    [Fact]
    public async Task E2E_CompleteRobotsFlow_AllowsPermittedPath()
    {
        // Arrange: Setup robots.txt and a valid page response
        var robotsContent = @"
User-agent: *
Disallow: /admin/
Allow: /public/";

        SetupRobotsResponse("example.com", robotsContent);
        SetupPageResponse("https://example.com/public/info.html", 
            "<html><head><title>Public Information</title></head><body>Welcome!</body></html>");

        // Act: Access an allowed page
        var request = new Request("https://example.com/public/info.html");
        var response = await _downloader.DownloadAsync(request);

        // Assert: Request should succeed
        Assert.True((int)response.StatusCode >= 200 && (int)response.StatusCode < 300);
        Assert.Contains("Public Information", response.Content.ToString());
        
        // Verify robots.txt was fetched
        VerifyRobotsFetched("example.com");
    }

    [Fact]
    public async Task E2E_CrawlDelayTiming_EnforcesCrawlDelay()
    {
        // Arrange: Setup robots.txt with crawl delay
        var robotsContent = @"
User-agent: *
Crawl-delay: 0.3";

        SetupRobotsResponse("example.com", robotsContent);
        SetupPageResponse("https://example.com/page1.html", "<html><title>Page 1</title></html>");
        SetupPageResponse("https://example.com/page2.html", "<html><title>Page 2</title></html>");

        // Act: Make two sequential requests
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        var request1 = new Request("https://example.com/page1.html");
        var response1 = await _downloader.DownloadAsync(request1);
        
        var request2 = new Request("https://example.com/page2.html");
        var response2 = await _downloader.DownloadAsync(request2);
        
        stopwatch.Stop();

        // Assert: Both requests succeed and delay was applied
        Assert.True((int)response1.StatusCode >= 200 && (int)response1.StatusCode < 300);
        Assert.True((int)response2.StatusCode >= 200 && (int)response2.StatusCode < 300);
        
        // Should have waited at least 250ms (allowing some tolerance)
        Assert.True(stopwatch.ElapsedMilliseconds >= 250, 
            $"Expected at least 250ms delay, but took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task E2E_UserAgentSpecificRules_AppliesToCorrectUserAgent()
    {
        // Arrange: Setup robots.txt with user-agent specific rules
        var robotsContent = @"
User-agent: BadBot
Disallow: /

User-agent: GoodBot
Disallow: /private/

User-agent: *
Disallow: /admin/";

        SetupRobotsResponse("example.com", robotsContent);
        SetupPageResponse("https://example.com/admin/panel.html", "<html><title>Admin Panel</title></html>");

        // Act & Assert: GoodBot should be allowed to access /admin/ but not /private/
        var goodBotAdminRequest = new Request("https://example.com/admin/panel.html");
        goodBotAdminRequest.Headers.Add("User-Agent", "GoodBot/1.0");
        var goodBotAdminResponse = await _downloader.DownloadAsync(goodBotAdminRequest);
        
        Assert.True((int)goodBotAdminResponse.StatusCode >= 200 && (int)goodBotAdminResponse.StatusCode < 300, 
            "GoodBot should be allowed to access /admin/");

        var goodBotPrivateRequest = new Request("https://example.com/private/data.html");
        goodBotPrivateRequest.Headers.Add("User-Agent", "GoodBot/1.0");
        var goodBotPrivateResponse = await _downloader.DownloadAsync(goodBotPrivateRequest);
        
        Assert.Equal(HttpStatusCode.Forbidden, goodBotPrivateResponse.StatusCode);
        Assert.Equal("Blocked by robots.txt", goodBotPrivateResponse.ReasonPhrase);
    }

    [Fact]
    public async Task E2E_RobotsTxtNotFound_AllowsAllRequests()
    {
        // Arrange: Setup 404 for robots.txt
        SetupRobotsNotFound("example.com");
        SetupPageResponse("https://example.com/any/path.html", "<html><title>Any Path</title></html>");

        // Act: Try to access any path
        var request = new Request("https://example.com/any/path.html");
        var response = await _downloader.DownloadAsync(request);

        // Assert: Should be allowed since robots.txt is not found
        Assert.True((int)response.StatusCode >= 200 && (int)response.StatusCode < 300);
        Assert.Contains("Any Path", response.Content.ToString());
    }

    [Fact]
    public async Task E2E_RobotsTxtCaching_ReusesCache()
    {
        // Arrange: Setup robots.txt
        var robotsContent = @"
User-agent: *
Disallow: /blocked/";

        SetupRobotsResponse("example.com", robotsContent);

        // Act: Make multiple requests to the same domain
        var request1 = new Request("https://example.com/blocked/page1.html");
        var request2 = new Request("https://example.com/blocked/page2.html");
        var request3 = new Request("https://example.com/blocked/page3.html");

        var response1 = await _downloader.DownloadAsync(request1);
        var response2 = await _downloader.DownloadAsync(request2);
        var response3 = await _downloader.DownloadAsync(request3);

        // Assert: All should be blocked
        Assert.Equal(HttpStatusCode.Forbidden, response1.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response2.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response3.StatusCode);

        // Verify robots.txt was only fetched once (cached for subsequent requests)
        _httpMock.Protected()
            .Verify("SendAsync", Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString().Contains("robots.txt")),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task E2E_NetworkError_FallsBackGracefully()
    {
        // Arrange: Setup network error for robots.txt
        _httpMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString().Contains("robots.txt")),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        SetupPageResponse("https://example.com/some/page.html", "<html><title>Some Page</title></html>");

        // Act: Try to access a page
        var request = new Request("https://example.com/some/page.html");
        var response = await _downloader.DownloadAsync(request);

        // Assert: Should be allowed since robots.txt fetch failed gracefully
        Assert.True((int)response.StatusCode >= 200 && (int)response.StatusCode < 300);
        Assert.Contains("Some Page", response.Content.ToString());
    }

    private void SetupRobotsResponse(string host, string content)
    {
        _httpMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString() == $"https://{host}/robots.txt"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(content)
            });
    }

    private void SetupRobotsNotFound(string host)
    {
        _httpMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString() == $"https://{host}/robots.txt"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private void SetupPageResponse(string url, string content)
    {
        _httpMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString() == url),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(content)
            });
    }

    private void VerifyRobotsFetched(string host)
    {
        _httpMock.Protected()
            .Verify("SendAsync", Times.AtLeastOnce(),
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString() == $"https://{host}/robots.txt"),
                ItExpr.IsAny<CancellationToken>());
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}