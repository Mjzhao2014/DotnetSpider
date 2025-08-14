using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.DataFlow;
using DotnetSpider.DataFlow.Parser;
using DotnetSpider.Downloader;
using DotnetSpider.Http;
using DotnetSpider.Proxy;
using DotnetSpider.Robots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace DotnetSpider.Tests;

    
/// <summary>
/// Comprehensive E2E tests for UseRobotsTxt() method testing real scenarios
/// </summary>
public class UseRobotsTxtE2ETests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _httpMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<IHttpClientFactory> _httpFactoryMock;
    private readonly List<string> _accessedUrls;
    private readonly List<DateTime> _requestTimes;

    public UseRobotsTxtE2ETests()
    {
        _httpMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMock.Object);
        
        _httpFactoryMock = new Mock<IHttpClientFactory>();
        _httpFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(_httpClient);
        
        _accessedUrls = new List<string>();
        _requestTimes = new List<DateTime>();
    }


    [Fact]
    public async Task UseRobotsTxt_E2E_RespectsDisallowRules()
    {
        // Arrange: Setup robots.txt with specific disallow rules
        var robotsContent = @"
User-agent: *
Disallow: /admin/
Disallow: /private/
Disallow: *.pdf
Allow: /public/
Crawl-delay: 0.1";

        SetupRobotsResponse("example.com", robotsContent);
        
        // Setup allowed pages
        SetupPageResponse("https://example.com/", 
            "<html><body><h1>Home</h1><a href='/public/info.html'>Public Info</a><a href='/admin/panel.html'>Admin</a><a href='/private/data.html'>Private</a></body></html>");
        SetupPageResponse("https://example.com/public/info.html", 
            "<html><body><h1>Public Info</h1></body></html>");

        // Setup disallow pages
        SetupPageResponse("https://example.com/private/info.html", 
            "<html><body><h1>private Info</h1></body></html>");
        SetupPageResponse("https://example.com/admin/info.html", 
            "<html><body><h1>admin Info</h1></body></html>");
        SetupPageResponse("https://example.com/public/info.pdf", 
            "<html><body><h1>Public pdf</h1></body></html>");
        
        
        // Do NOT setup responses for blocked URLs - they should never be requested

        TestContext.Current = this;

        // Act: Create spider with UseRobotsTxt() and run it
        var builder = Builder.CreateDefaultBuilder<ComprehensiveTestSpider>(options =>
        {
            options.Speed = 1;
            options.Depth = 3;
        });
        
        // This is the method being tested - should make spider respect robots.txt
        builder.UseRobotsTxt();
        
        // Configure mock HTTP client and ensure proper wiring
        builder.ConfigureServices(services =>
        {
            // Replace the default HttpClientFactory with our mock
            services.AddSingleton(_httpFactoryMock.Object);
            
            // Ensure HttpClient services are properly configured
            services.AddHttpClient();
        });

        var spider = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        
        await spider.RunAsync(cts.Token);

        // Assert: First verify basic functionality - spider ran and accessed some URLs
        Assert.NotEmpty(_accessedUrls);
        
        // Should access allowed URLs
        Assert.Contains(_accessedUrls, url => url.Contains("example.com/"));
        Assert.Contains(_accessedUrls, url => url.Contains("example.com/public"));


        Assert.DoesNotContain(_accessedUrls, url => url.Contains("example.com/private"));
        Assert.DoesNotContain(_accessedUrls, url => url.Contains("example.com/admin"));
        Assert.DoesNotContain(_accessedUrls, url => url.Contains(".pdf"));
        
    }

    [Fact]
    public async Task UseRobotsTxt_E2E_RespectsCrawlDelay()
    {
        // Arrange: Setup robots.txt with crawl delay
        var robotsContent = @"
User-agent: *
Crawl-delay: 1
Disallow: /blocked/";

        SetupRobotsResponse("test-delay.com", robotsContent);
        
        // Setup multiple pages to test delay between requests
        SetupPageResponse("https://test-delay.com/page1.html", 
            "<html><body><h1>Page 1</h1><a href='/page2.html'>Page 2</a></body></html>");
        SetupPageResponse("https://test-delay.com/page2.html", 
            "<html><body><h1>Page 2</h1><a href='/page3.html'>Page 3</a></body></html>");
        SetupPageResponse("https://test-delay.com/page3.html", 
            "<html><body><h1>Page 3</h1></body></html>");

        TestContext.Current = this;

        // Act: Create spider with UseRobotsTxt()
        var builder = Builder.CreateDefaultBuilder<CrawlDelayTestSpider>(options =>
        {
            options.Speed = 10; // High speed to test that crawl-delay overrides it
            options.Depth = 3;
        });
        
        builder.UseRobotsTxt();
        
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_httpFactoryMock.Object);
            services.AddHttpClient();
        });

        var spider = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        
        var startTime = DateTime.UtcNow;
        await spider.RunAsync(cts.Token);
        var endTime = DateTime.UtcNow;

        // Assert: Verify basic functionality first
        Assert.NotEmpty(_accessedUrls);
        Console.WriteLine($"Request times count: {_requestTimes.Count}");
        Console.WriteLine($"Accessed URLs: {string.Join(", ", _accessedUrls)}");
        
        if (_requestTimes.Count >= 2)
        {
            // Check timing between requests
            for (int i = 1; i < _requestTimes.Count; i++)
            {
                var timeDiff = _requestTimes[i] - _requestTimes[i - 1];
                // Console.WriteLine($"Time between request {i-1} and {i}: {timeDiff.TotalMilliseconds}ms");

                 // TestBot has crawl-delay: 1, so should be >= 1000ms (allowing some tolerance)
                Assert.True(timeDiff.TotalMilliseconds >= 950);
            }
        }
    }

    [Fact]
    public async Task UseRobotsTxt_E2E_UserAgentSpecificRules_TestBot()
    {
        // Arrange: Setup robots.txt with user-agent specific rules
        // TestBot is only blocked from /secret/, but allowed to access /admin/
        // * (all other bots) are blocked from /admin/ but can access /secret/
        var robotsContent = @"
User-agent: TestBot
Disallow: /secret/
Crawl-delay: 1

User-agent: *
Disallow: /admin/
Crawl-delay: 0.5";

        SetupRobotsResponse("agent-test.com", robotsContent);

        // Setup all pages that might be accessed
        SetupPageResponse("https://agent-test.com/",
            "<html><body><h1>Home</h1><a href='/secret/data.html'>Secret</a><a href='/admin/panel.html'>Admin</a><a href='/public/info.html'>Public</a></body></html>");
        SetupPageResponse("https://agent-test.com/public/info.html",
            "<html><body><h1>Public Info</h1></body></html>");
        SetupPageResponse("https://agent-test.com/admin/panel.html",
            "<html><body><h1>Admin Panel</h1></body></html>");
        SetupPageResponse("https://agent-test.com/secret/data.html",
            "<html><body><h1>Secret Data</h1></body></html>");

        TestContext.Current = this;

        // Act: Create spider with TestBot user agent
        var builder = Builder.CreateDefaultBuilder<UserAgentTestSpider>(options =>
        {
            options.Speed = 10;
            options.Depth = 2;
        });

        builder.UseRobotsTxt();

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_httpFactoryMock.Object);
            services.AddHttpClient();
        });

        var spider = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await spider.RunAsync(cts.Token);

        // Assert: Verify user-agent specific rules are respected
        Assert.NotEmpty(_accessedUrls);

        // Check what URLs were accessed
        var secretAccessed = _accessedUrls.Any(url => url.Contains("/secret/"));
        var adminAccessed = _accessedUrls.Any(url => url.Contains("/admin/"));
        var publicAccessed = _accessedUrls.Any(url => url.Contains("/public/"));

        Assert.False(secretAccessed);
        Assert.True(adminAccessed);
        Assert.True(publicAccessed);

        // Verify crawl-delay specific to TestBot (1 second) is respected
        Assert.True(_requestTimes.Count >= 2, "Should have at least 2 requests to test crawl delay");
        
        for (int i = 1; i < _requestTimes.Count; i++)
        {
            var timeDiff = _requestTimes[i] - _requestTimes[i - 1];
            // TestBot has crawl-delay: 1, so should be >= 1000ms (allowing some tolerance)
            Assert.True(timeDiff.TotalMilliseconds >= 950, 
                $"TestBot crawl delay not respected: time between request {i-1} and {i} was {timeDiff.TotalMilliseconds}ms, expected >= 1000ms for TestBot");
        }
    }


    [Fact]
    public async Task UseRobotsTxt_E2E_UserAgentSpecificRules_OtherAgent()
    {
        // Arrange: Setup robots.txt with user-agent specific rules
        // TestBot is only blocked from /secret/, but allowed to access /admin/
        // * (all other bots) are blocked from /admin/ but can access /secret/
        var robotsContent = @"
User-agent: TestBot
Disallow: /secret/
Crawl-delay: 1

User-agent: *
Disallow: /admin/
Crawl-delay: 0.5";

        SetupRobotsResponse("agent-test.com", robotsContent);

        // Setup all pages that might be accessed
        SetupPageResponse("https://agent-test.com/",
            "<html><body><h1>Home</h1><a href='/secret/data.html'>Secret</a><a href='/admin/panel.html'>Admin</a><a href='/public/info.html'>Public</a></body></html>");
        SetupPageResponse("https://agent-test.com/public/info.html",
            "<html><body><h1>Public Info</h1></body></html>");
        SetupPageResponse("https://agent-test.com/admin/panel.html",
            "<html><body><h1>Admin Panel</h1></body></html>");
        SetupPageResponse("https://agent-test.com/secret/data.html",
            "<html><body><h1>Secret Data</h1></body></html>");

        TestContext.Current = this;

        // Act: Create spider with TestBot user agent
        var builder = Builder.CreateDefaultBuilder<ComprehensiveTestSpider>(options =>
        {
            options.Speed = 10;
            options.Depth = 2;
        });

        builder.UseRobotsTxt();

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_httpFactoryMock.Object);
            services.AddHttpClient();
        });

        var spider = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await spider.RunAsync(cts.Token);

        // Assert: Verify user-agent specific rules are respected
        Assert.NotEmpty(_accessedUrls);

        // Check what URLs were accessed
        var secretAccessed = _accessedUrls.Any(url => url.Contains("/secret/"));
        var adminAccessed = _accessedUrls.Any(url => url.Contains("/admin/"));
        var publicAccessed = _accessedUrls.Any(url => url.Contains("/public/"));

        Assert.True(secretAccessed);
        Assert.False(adminAccessed);
        Assert.True(publicAccessed);

        // Verify crawl-delay specific to TestBot (1 second) is respected
        Assert.True(_requestTimes.Count >= 2, "Should have at least 2 requests to test crawl delay");
        
        for (int i = 1; i < _requestTimes.Count; i++)
        {
            var timeDiff = _requestTimes[i] - _requestTimes[i - 1];
            // TestBot has crawl-delay: 1, so should be >= 450ms (allowing some tolerance)
            Assert.True(timeDiff.TotalMilliseconds >= 450);
        }
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

    private void SetupPageResponse(string url, string content)
    {
        _httpMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString() == url && !req.RequestUri.ToString().Contains("robots.txt")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                // Track accessed URLs and request times
                TestContext.Current?._accessedUrls.Add(url);
                TestContext.Current?._requestTimes.Add(DateTime.UtcNow);
                
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(content, System.Text.Encoding.UTF8, "text/html")
                };
            });
    }
    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    /// <summary>
    /// Test spider for comprehensive robots.txt testing
    /// </summary>
    public class ComprehensiveTestSpider : Spider
    {
        public ComprehensiveTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://example.com/"));
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Test spider for crawl delay testing
    /// </summary>
    public class CrawlDelayTestSpider : Spider
    {
        public CrawlDelayTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://test-delay.com/page1.html"));
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Test spider with specific user agent for testing user-agent rules
    /// </summary>
    public class UserAgentTestSpider : Spider
    {
        public UserAgentTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://agent-test.com/")
            {
                Headers = { ["User-Agent"] = "TestBot/1.0" }
            });
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Data parser that follows links for comprehensive testing
    /// </summary>
    private class TestDataParser : DataParser
    {
        protected override Task ParseAsync(DataFlowContext context)
        {
            // Follow all links found on the page
            var links = context.Selectable.XPath(".//a[@href]").Links();
            foreach (var link in links)
            {
                if (Uri.TryCreate(context.Request.RequestUri, link, out var absoluteUri))
                {
                    context.AddFollowRequests(new Request(absoluteUri.ToString()));
                }
            }

            return Task.CompletedTask;
        }

        public override Task InitializeAsync()
        {
            AddRequiredValidator("^https?://");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Static context to pass test instance to spiders
    /// </summary>
    private static class TestContext
    {
        public static UseRobotsTxtE2ETests Current { get; set; }
    }
}