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
    public async Task KeepExistingBehavior_NotUseRobotsTxt_E2E()
    {
        // Arrange: Setup robots.txt with specific disallow rules (but spider won't use it)
        var robotsContent = @"
User-agent: *
Disallow: /admin/
Disallow: /private/
Disallow: *.pdf
Allow: /public/
Crawl-delay: 0.1";

        SetupRobotsResponse("example.com", robotsContent);
        
        // Setup home page with links to all pages including blocked ones
        SetupPageResponse("https://example.com/", 
            @"<html><body>
                <h1>Home</h1>
                <a href='/public/info.html'>Public Info</a>
                <a href='/admin/info.html'>Admin</a>
                <a href='/private/info.html'>Private</a>
                <a href='/public/info.pdf'>PDF</a>
              </body></html>");
        SetupPageResponse("https://example.com/public/info.html", 
            "<html><body><h1>Public Info</h1></body></html>");

        // Setup pages that would be blocked by robots.txt (but should be accessed without UseRobotsTxt)
        SetupPageResponse("https://example.com/private/info.html", 
            "<html><body><h1>Private Info</h1></body></html>");
        SetupPageResponse("https://example.com/admin/info.html", 
            "<html><body><h1>Admin Info</h1></body></html>");
        SetupPageResponse("https://example.com/public/info.pdf", 
            "<html><body><h1>Public PDF</h1></body></html>");

        TestContext.Current = this;

        // Act: Create spider WITHOUT UseRobotsTxt() - should ignore robots.txt rules
        var builder = Builder.CreateDefaultBuilder<ComprehensiveTestSpider>(options =>
        {
            options.Speed = 1; // Slower to ensure proper processing
            options.Depth = 2; // Sufficient depth to reach all linked pages
        });
        
        // Intentionally NOT calling UseRobotsTxt() to test baseline behavior
        // builder.UseRobotsTxt();
        
        // Configure mock HTTP client and ensure proper wiring
        builder.ConfigureServices(services =>
        {
            // Replace the default HttpClientFactory with our mock
            services.AddSingleton(_httpFactoryMock.Object);
            
            // Ensure HttpClient services are properly configured
            services.AddHttpClient();
        });

        var spider = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // Longer timeout to ensure all links are processed
        
        await spider.RunAsync(cts.Token);

        // Assert: Without UseRobotsTxt(), spider should access ALL URLs including blocked ones
        Assert.NotEmpty(_accessedUrls);
        
        // Should access all URLs since robots.txt is ignored
        Assert.Contains(_accessedUrls, url => url.Contains("example.com/"));
        Assert.Contains(_accessedUrls, url => url.Contains("example.com/public"));
        Assert.Contains(_accessedUrls, url => url.Contains("example.com/private"));
        Assert.Contains(_accessedUrls, url => url.Contains("example.com/admin"));
        Assert.Contains(_accessedUrls, url => url.Contains(".pdf"));
        
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
        
        // Setup home page with links to all pages including blocked ones
        SetupPageResponse("https://example.com/", 
            @"<html><body>
                <h1>Home</h1>
                <a href='/public/info.html'>Public Info</a>
                <a href='/admin/info.html'>Admin</a>
                <a href='/private/info.html'>Private</a>
                <a href='/public/info.pdf'>PDF</a>
              </body></html>");
        SetupPageResponse("https://example.com/public/info.html", 
            "<html><body><h1>Public Info</h1></body></html>");

        // Setup pages that would be blocked by robots.txt (but should be accessed without UseRobotsTxt)
        SetupPageResponse("https://example.com/private/info.html", 
            "<html><body><h1>Private Info</h1></body></html>");
        SetupPageResponse("https://example.com/admin/info.html", 
            "<html><body><h1>Admin Info</h1></body></html>");
        SetupPageResponse("https://example.com/public/info.pdf", 
            "<html><body><h1>Public PDF</h1></body></html>");

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

        Assert.Equal(3, _accessedUrls.Count());
        
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

        // TestBot should be blocked from /secret/ (per TestBot-specific rules)
        Assert.False(secretAccessed);
        // TestBot should be allowed to access /admin/ (wildcard rules don't apply to TestBot)
        Assert.True(adminAccessed);
        // TestBot should be allowed to access /public/ (no restrictions)
        Assert.True(publicAccessed);

        // Verify crawl-delay specific to TestBot (1 second) is respected
        Assert.True(_requestTimes.Count >= 2, "Should have at least 2 requests to test crawl delay");
        
        for (int i = 1; i < _requestTimes.Count; i++)
        {
            var timeDiff = _requestTimes[i] - _requestTimes[i - 1];
            // TestBot has crawl-delay: 1, so should be >= 1000ms (allowing some tolerance)
            Assert.True(timeDiff.TotalMilliseconds >= 900, 
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
Crawl-delay: 1.5";

        SetupRobotsResponse("agent-test.com", robotsContent);

        // Setup all pages that might be accessed
        SetupPageResponse("https://agent-test.com/",
            "<html><body><h1>Home</h1><a href='/secret/data.html'>Secret</a><a href='/admin/panel.html'>Admin</a><a href='/public/info.html'>Public</a></body></html>");
        SetupPageResponse("https://agent-test.com/public/info.html",
            "<html><body><h1>Public Info</h1></body></html>");
        SetupPageResponse("https://agent-test.com/admin/info.html",
            "<html><body><h1>Admin Panel</h1></body></html>");
        SetupPageResponse("https://agent-test.com/secret/info.html",
            "<html><body><h1>Secret Data</h1></body></html>");

        TestContext.Current = this;

        // Act: Create spider with default user agent (not TestBot) to test wildcard rules
        var builder = Builder.CreateDefaultBuilder<OtherAgentTestSpider>(options =>
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

        Assert.Equal(3, _accessedUrls.Count);

        // Check what URLs were accessed
        var secretAccessed = _accessedUrls.Any(url => url.Contains("/secret/"));
        var adminAccessed = _accessedUrls.Any(url => url.Contains("/admin/"));
        var publicAccessed = _accessedUrls.Any(url => url.Contains("/public/"));

        // For agents other than TestBot, wildcard (*) rules apply:
        // - Should access /secret/ (only TestBot is blocked from /secret/)
        // - Should NOT access /admin/ (wildcard * blocks all other agents from /admin/)
        // - Should access /public/ (no restrictions)
        Assert.True(secretAccessed);
        Assert.False(adminAccessed);
        Assert.True(publicAccessed);

        // Verify crawl-delay specific to wildcard (*) rules (0.5 seconds) is respected
        Assert.True(_requestTimes.Count >= 2, "Should have at least 2 requests to test crawl delay");
        
        for (int i = 1; i < _requestTimes.Count; i++)
        {
            var timeDiff = _requestTimes[i] - _requestTimes[i - 1];
            // OtherAgent follows wildcard (*) rules with crawl-delay: 1.5, so should be >= 1500ms (allowing some tolerance)
            Assert.True(timeDiff.TotalMilliseconds >= 1400,
                $"OtherAgent crawl delay not respected: time between request {i-1} and {i} was {timeDiff.TotalMilliseconds}ms, expected >= 1500ms for wildcard rules");
        }
    }

    [Fact]
    public async Task UseRobotsTxt_E2E_AllowOverridesDisallow()
    {
        // Arrange: Test Allow directive that overrides broader Disallow rules
        var robotsContent = @"
User-agent: *
Disallow: /private/
Allow: /private/public-info/
Crawl-delay: 0.1";

        SetupRobotsResponse("allow-test.com", robotsContent);
        
        SetupPageResponse("https://allow-test.com/", 
            @"<html><body>
                <h1>Home</h1>
                <a href='/private/secret.html'>Private Secret</a>
                <a href='/private/public-info/data.html'>Public Info in Private</a>
                <a href='/public/info.html'>Public</a>
              </body></html>");
        SetupPageResponse("https://allow-test.com/public/info.html", 
            "<html><body><h1>Public Info</h1></body></html>");
        SetupPageResponse("https://allow-test.com/private/secret.html", 
            "<html><body><h1>Private Secret</h1></body></html>");
        SetupPageResponse("https://allow-test.com/private/public-info/data.html", 
            "<html><body><h1>Public Info in Private Area</h1></body></html>");

        TestContext.Current = this;

        var builder = Builder.CreateDefaultBuilder<AllowTestSpider>(options =>
        {
            options.Speed = 1;
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

        // Assert: Verify Allow overrides Disallow
        Assert.NotEmpty(_accessedUrls);
        
        // Should access home and public pages
        Assert.Contains(_accessedUrls, url => url.Contains("allow-test.com/"));
        Assert.Contains(_accessedUrls, url => url.Contains("/public/"));
        
        // Should NOT access /private/secret.html (blocked by Disallow: /private/)
        Assert.DoesNotContain(_accessedUrls, url => url.Contains("/private/secret.html"));
        
        // Should access /private/public-info/ (allowed by Allow: /private/public-info/)
        Assert.Contains(_accessedUrls, url => url.Contains("/private/public-info/"));
    }

    [Fact]
    public async Task UseRobotsTxt_E2E_CaseInsensitiveMatching()
    {
        // Arrange: Test case-insensitive user-agent matching
        var robotsContent = @"
User-agent: testbot
Disallow: /secret/
Crawl-delay: 0.5

User-agent: *
Disallow: /admin/
Crawl-delay: 0.1";

        SetupRobotsResponse("case-test.com", robotsContent);
        
        SetupPageResponse("https://case-test.com/", 
            @"<html><body>
                <h1>Home</h1>
                <a href='/secret/data.html'>Secret</a>
                <a href='/admin/panel.html'>Admin</a>
                <a href='/public/info.html'>Public</a>
              </body></html>");
        SetupPageResponse("https://case-test.com/public/info.html", 
            "<html><body><h1>Public Info</h1></body></html>");
        SetupPageResponse("https://case-test.com/admin/panel.html", 
            "<html><body><h1>Admin Panel</h1></body></html>");
        SetupPageResponse("https://case-test.com/secret/data.html", 
            "<html><body><h1>Secret Data</h1></body></html>");

        TestContext.Current = this;

        // Act: Use "TESTBOT" (uppercase) to test case-insensitive matching with "testbot" (lowercase) in robots.txt
        var builder = Builder.CreateDefaultBuilder<CaseTestSpider>(options =>
        {
            options.Speed = 1;
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

        // Assert: Verify case-insensitive matching works
        Assert.NotEmpty(_accessedUrls);
        
        // TESTBOT should match "testbot" rules (case-insensitive)
        // Should NOT access /secret/ (blocked for testbot)
        Assert.DoesNotContain(_accessedUrls, url => url.Contains("/secret/"));
        
        // Should access /admin/ (testbot is not blocked from admin, only * is)
        Assert.Contains(_accessedUrls, url => url.Contains("/admin/"));
        
        // Should access /public/
        Assert.Contains(_accessedUrls, url => url.Contains("/public/"));
    }

    [Fact]
    public async Task UseRobotsTxt_E2E_PrefixMatching()
    {
        // Arrange: Test prefix matching for user-agents
        var robotsContent = @"
User-agent: GoogleBot
Disallow: /google-blocked/

User-agent: Test
Disallow: /test-blocked/

User-agent: *
Disallow: /wildcard-blocked/
Crawl-delay: 0.1";

        SetupRobotsResponse("prefix-test.com", robotsContent);
        
        SetupPageResponse("https://prefix-test.com/", 
            @"<html><body>
                <h1>Home</h1>
                <a href='/google-blocked/data.html'>Google Blocked</a>
                <a href='/test-blocked/data.html'>Test Blocked</a>
                <a href='/wildcard-blocked/data.html'>Wildcard Blocked</a>
                <a href='/public/info.html'>Public</a>
              </body></html>");
        SetupPageResponse("https://prefix-test.com/public/info.html", 
            "<html><body><h1>Public Info</h1></body></html>");
        SetupPageResponse("https://prefix-test.com/google-blocked/data.html", 
            "<html><body><h1>Google Blocked Data</h1></body></html>");
        SetupPageResponse("https://prefix-test.com/test-blocked/data.html", 
            "<html><body><h1>Test Blocked Data</h1></body></html>");
        SetupPageResponse("https://prefix-test.com/wildcard-blocked/data.html", 
            "<html><body><h1>Wildcard Blocked Data</h1></body></html>");

        TestContext.Current = this;

        // Act: Use "TestBot/1.0" user-agent which should match "Test" prefix
        var builder = Builder.CreateDefaultBuilder<PrefixTestSpider>(options =>
        {
            options.Speed = 1;
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

        // Assert: Verify prefix matching works
        Assert.NotEmpty(_accessedUrls);
        
        // "TestBot/1.0" should match "Test" prefix rules
        // Should NOT access /test-blocked/ (blocked for Test prefix)
        Assert.DoesNotContain(_accessedUrls, url => url.Contains("/test-blocked/"));
        
        // Should access /google-blocked/ (not blocked for Test prefix)
        Assert.Contains(_accessedUrls, url => url.Contains("/google-blocked/"));
        
        // Should NOT access /wildcard-blocked/ (since specific Test rule takes precedence over *)
        // Wait, this is wrong - if Test matches, wildcard rules don't apply
        // Let me check the spec again... Actually, Test rules should take precedence
        // So it should access /wildcard-blocked/ since only /test-blocked/ is blocked for Test
        Assert.Contains(_accessedUrls, url => url.Contains("/wildcard-blocked/"));
        
        // Should access /public/
        Assert.Contains(_accessedUrls, url => url.Contains("/public/"));
    }

    [Fact]
    public async Task UseRobotsTxt_E2E_MissingRobotsFile()
    {
        // Arrange: Setup to return 404 for robots.txt
        _httpMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString().Contains("robots.txt")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        
        SetupPageResponse("https://missing-robots.com/", 
            @"<html><body>
                <h1>Home</h1>
                <a href='/admin/panel.html'>Admin</a>
                <a href='/private/data.html'>Private</a>
                <a href='/secret/info.html'>Secret</a>
              </body></html>");
        SetupPageResponse("https://missing-robots.com/admin/panel.html", 
            "<html><body><h1>Admin Panel</h1></body></html>");
        SetupPageResponse("https://missing-robots.com/private/data.html", 
            "<html><body><h1>Private Data</h1></body></html>");
        SetupPageResponse("https://missing-robots.com/secret/info.html", 
            "<html><body><h1>Secret Info</h1></body></html>");

        TestContext.Current = this;

        var builder = Builder.CreateDefaultBuilder<MissingRobotsTestSpider>(options =>
        {
            options.Speed = 1;
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

        // Assert: When robots.txt is missing, full access should be allowed
        Assert.NotEmpty(_accessedUrls);
        
        // Should access ALL URLs when robots.txt is missing
        Assert.Contains(_accessedUrls, url => url.Contains("missing-robots.com/"));
        Assert.Contains(_accessedUrls, url => url.Contains("/admin/"));
        Assert.Contains(_accessedUrls, url => url.Contains("/private/"));
        Assert.Contains(_accessedUrls, url => url.Contains("/secret/"));
    }

    [Fact]
    public async Task UseRobotsTxt_E2E_EmptyRobotsFile()
    {
        // Arrange: Setup empty robots.txt
        SetupRobotsResponse("empty-robots.com", "");
        
        SetupPageResponse("https://empty-robots.com/", 
            @"<html><body>
                <h1>Home</h1>
                <a href='/admin/panel.html'>Admin</a>
                <a href='/private/data.html'>Private</a>
              </body></html>");
        SetupPageResponse("https://empty-robots.com/admin/panel.html", 
            "<html><body><h1>Admin Panel</h1></body></html>");
        SetupPageResponse("https://empty-robots.com/private/data.html", 
            "<html><body><h1>Private Data</h1></body></html>");

        TestContext.Current = this;

        var builder = Builder.CreateDefaultBuilder<EmptyRobotsTestSpider>(options =>
        {
            options.Speed = 1;
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

        // Assert: Empty robots.txt should allow full access
        Assert.NotEmpty(_accessedUrls);
        
        // Should access ALL URLs when robots.txt is empty
        Assert.Contains(_accessedUrls, url => url.Contains("empty-robots.com/"));
        Assert.Contains(_accessedUrls, url => url.Contains("/admin/"));
        Assert.Contains(_accessedUrls, url => url.Contains("/private/"));
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
    /// Test spider for Allow directive testing
    /// </summary>
    public class AllowTestSpider : Spider
    {
        public AllowTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://allow-test.com/"));
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Test spider for case-insensitive user-agent matching
    /// </summary>
    public class CaseTestSpider : Spider
    {
        public CaseTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://case-test.com/")
            {
                Headers = { ["User-Agent"] = "TESTBOT" } // Uppercase to test case-insensitive matching
            });
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Test spider for prefix matching user-agent rules
    /// </summary>
    public class PrefixTestSpider : Spider
    {
        public PrefixTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://prefix-test.com/")
            {
                Headers = { ["User-Agent"] = "TestBot/1.0" } // Should match "Test" prefix
            });
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Test spider for missing robots.txt file handling
    /// </summary>
    public class MissingRobotsTestSpider : Spider
    {
        public MissingRobotsTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://missing-robots.com/"));
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Test spider for empty robots.txt file handling
    /// </summary>
    public class EmptyRobotsTestSpider : Spider
    {
        public EmptyRobotsTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://empty-robots.com/"));
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Test spider for most specific user-agent matching
    /// </summary>
    public class SpecificTestSpider : Spider
    {
        public SpecificTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            await AddRequestsAsync(new Request("https://specific-test.com/")
            {
                Headers = { ["User-Agent"] = "TestBot" } // Should match "TestBot" exactly (most specific)
            });
            AddDataFlow<TestDataParser>();
        }
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
                Headers = { ["User-Agent"] = "TestBot" }
            });
            AddDataFlow<TestDataParser>();
        }
    }

    /// <summary>
    /// Test spider with default user agent for testing wildcard (*) rules against other agents
    /// </summary>
    public class OtherAgentTestSpider : Spider
    {
        public OtherAgentTestSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
            : base(options, services, logger)
        {
        }

        protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
        {
            // Use default user agent (not TestBot) to test wildcard rules
            await AddRequestsAsync(new Request("https://agent-test.com/"));
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
            // For the home page, directly add the specific URLs we want to test
            if (context.Request.RequestUri.AbsolutePath == "/")
            {
                string[] urlsToTest;
                var host = context.Request.RequestUri.Host;
                
                // Define URLs to test based on the host/test scenario
                if (host.Contains("allow-test"))
                {
                    urlsToTest = new[]
                    {
                        "/public/info.html",
                        "/private/secret.html",
                        "/private/public-info/data.html"
                    };
                }
                else if (host.Contains("case-test") || host.Contains("prefix-test") || host.Contains("specific-test"))
                {
                    urlsToTest = new[]
                    {
                        "/public/info.html",
                        "/secret/data.html",
                        "/admin/panel.html",
                        "/testbot-blocked/data.html",
                        "/test-blocked/data.html",
                        "/wildcard-blocked/data.html"
                    };
                }
                else if (host.Contains("missing-robots") || host.Contains("empty-robots"))
                {
                    urlsToTest = new[]
                    {
                        "/admin/panel.html",
                        "/private/data.html",
                        "/secret/info.html"
                    };
                }
                else
                {
                    // Default test URLs for existing tests
                    urlsToTest = new[]
                    {
                        "/public/info.html",
                        "/admin/info.html", 
                        "/private/info.html",
                        "/secret/info.html",
                        "/public/info.pdf"
                    };
                }
                
                foreach (var url in urlsToTest)
                {
                    if (Uri.TryCreate(context.Request.RequestUri, url, out var absoluteUri))
                    {
                        context.AddFollowRequests(new Request(absoluteUri.ToString()));
                    }
                }
            }
            else
            {
                // For other pages, try to find links normally
                var links = context.Selectable.XPath(".//a[@href]").Links();
                
                foreach (var link in links)
                {
                    if (Uri.TryCreate(context.Request.RequestUri, link, out var absoluteUri))
                    {
                        context.AddFollowRequests(new Request(absoluteUri.ToString()));
                    }
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