using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.DataFlow;
using DotnetSpider.DataFlow.Parser;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.Robots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace DotnetSpider.Tests;

[DisplayName("RobotsTxt Integration Test Spider")]
public class RobotsTxtIntegrationTestSpider : Spider
{
    public RobotsTxtIntegrationTestSpider(
        IOptions<SpiderOptions> options,
        DependenceServices services,
        ILogger<Spider> logger)
        : base(options, services, logger)
    {
    }

    protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
    {
        AddDataFlow<TestDataParser>();
        AddDataFlow<TestResultStorage>();

        // Add test URLs - some should be allowed, some blocked
        await AddRequestsAsync(
            new Request("https://testsite.com/public/page1.html") { Owner = SpiderId.ToString() },
            new Request("https://testsite.com/admin/panel.html") { Owner = SpiderId.ToString() },      // Should be blocked
            new Request("https://testsite.com/private/data.html") { Owner = SpiderId.ToString() },     // Should be blocked
            new Request("https://testsite.com/public/page2.html") { Owner = SpiderId.ToString() },
            new Request("https://testsite.com/api/endpoint") { Owner = SpiderId.ToString() },          // Should be blocked
            new Request("https://testsite.com/about.html") { Owner = SpiderId.ToString() }
        );
    }

    protected override SpiderId GenerateSpiderId()
    {
        return new SpiderId(ObjectId.CreateId().ToString(), "RobotsTxt Integration Test");
    }

    public class TestDataParser : DataParser
    {
        public static List<string> ProcessedUrls { get; } = new();
        public static List<string> BlockedUrls { get; } = new();
        public static List<string> SuccessfulUrls { get; } = new();

        protected override Task ParseAsync(DataFlowContext context)
        {
            var url = context.Request.RequestUri.ToString();
            ProcessedUrls.Add(url);

            Console.WriteLine($"Processing URL: {url}");
            Console.WriteLine($"Response Status: {context.Response.StatusCode}");
            Console.WriteLine($"Response Reason: {context.Response.ReasonPhrase}");

            if (context.Response.StatusCode == HttpStatusCode.Forbidden && 
                context.Response.ReasonPhrase == "Blocked by robots.txt")
            {
                BlockedUrls.Add(url);
                Console.WriteLine($"URL blocked by robots.txt: {url}");
            }
            else if ((int)context.Response.StatusCode >= 200 && (int)context.Response.StatusCode < 300)
            {
                SuccessfulUrls.Add(url);
                Console.WriteLine($"URL successful: {url}");
                context.AddData("url", url);
                context.AddData("title", context.Selectable.XPath(".//title")?.Value ?? "No title");
                context.AddData("status", "success");
            }

            return Task.CompletedTask;
        }

        public override Task InitializeAsync()
        {
            // Clear static collections for each test
            ProcessedUrls.Clear();
            BlockedUrls.Clear();
            SuccessfulUrls.Clear();
            return Task.CompletedTask;
        }
    }

    private class TestResultStorage : IDataFlow
    {
        private ILogger _logger;

        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public void SetLogger(ILogger logger)
        {
            _logger = logger;
        }

        public Task HandleAsync(DataFlowContext context, ResponseDelegate next)
        {
            // Just pass through - we're collecting results in the spider itself
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
}

public class RobotsTxtIntegrationTests
{
    [Fact]
    public async Task Integration_SpiderWithRobotsTxt_RespectsRobotsRules()
    {
        // Arrange
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        
        // Setup robots.txt response
        var robotsContent = @"
User-agent: *
Disallow: /admin/
Disallow: /private/
Disallow: /api/
Allow: /public/
Crawl-delay: 0.1";

        SetupHttpResponse(mockHttpHandler, "https://testsite.com/robots.txt", robotsContent);
        
        // Setup page responses for allowed URLs
        SetupHttpResponse(mockHttpHandler, "https://testsite.com/public/page1.html", 
            "<html><head><title>Public Page 1</title></head><body>Public content</body></html>");
        SetupHttpResponse(mockHttpHandler, "https://testsite.com/public/page2.html", 
            "<html><head><title>Public Page 2</title></head><body>Public content</body></html>");
        SetupHttpResponse(mockHttpHandler, "https://testsite.com/about.html", 
            "<html><head><title>About Us</title></head><body>About content</body></html>");

        // Setup responses for blocked URLs (these should not be reached by the downloader)
        SetupHttpResponse(mockHttpHandler, "https://testsite.com/admin/panel.html", 
            "<html><head><title>Admin Panel</title></head><body>Admin content</body></html>");
        SetupHttpResponse(mockHttpHandler, "https://testsite.com/private/data.html", 
            "<html><head><title>Private Data</title></head><body>Private content</body></html>");
        SetupHttpResponse(mockHttpHandler, "https://testsite.com/api/endpoint", 
            "<html><head><title>API Endpoint</title></head><body>API content</body></html>");

        var httpClient = new HttpClient(mockHttpHandler.Object);
        
        // Create builder with robots.txt support
        var builder = Builder.CreateDefaultBuilder<RobotsTxtIntegrationTestSpider>(options =>
        {
            options.Speed = 100; // Very fast for testing
            options.EmptySleepTime = 100; // Short delay when no more requests
            options.Depth = 1; // Only crawl depth 1
        });

        builder.ConfigureServices(services =>
        {
            // Replace HTTP client factory with our mock
            services.AddSingleton<IHttpClientFactory>(provider =>
            {
                var factory = new Mock<IHttpClientFactory>();
                factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);
                return factory.Object;
            });
            
            // Add essential missing services
            services.AddSingleton<DotnetSpider.Proxy.IProxyService, DotnetSpider.Proxy.EmptyProxyService>();
            
            // Add debugging
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddConsole());
        });

        // Enable robots.txt compliance
        builder.UseRobotsTxt();

        using var host = builder.Build();
        var spider = host.Services.GetServices<IHostedService>()
            .OfType<RobotsTxtIntegrationTestSpider>()
            .FirstOrDefault();

        // Act
        var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        try
        {
            await host.StartAsync(cancellationTokenSource.Token);
            
            // Wait for spider to complete processing all requests
            await Task.Delay(5000, cancellationTokenSource.Token);
            
            await host.StopAsync(cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected timeout - spider should have finished processing
        }

        // Assert
        Assert.NotNull(spider);
        
        // Debug output
        var processedUrls = RobotsTxtIntegrationTestSpider.TestDataParser.ProcessedUrls;
        var successfulUrls = RobotsTxtIntegrationTestSpider.TestDataParser.SuccessfulUrls;
        var blockedUrls = RobotsTxtIntegrationTestSpider.TestDataParser.BlockedUrls;
        
        // Log the actual results for debugging
        Console.WriteLine($"Total processed URLs: {processedUrls.Count}");
        Console.WriteLine($"Processed URLs: [{string.Join(", ", processedUrls)}]");
        Console.WriteLine($"Successful URLs: [{string.Join(", ", successfulUrls)}]");
        Console.WriteLine($"Blocked URLs: [{string.Join(", ", blockedUrls)}]");
        
        // Verify that robots.txt rules were respected
        Assert.Contains("https://testsite.com/public/page1.html", RobotsTxtIntegrationTestSpider.TestDataParser.SuccessfulUrls);
        Assert.Contains("https://testsite.com/public/page2.html", RobotsTxtIntegrationTestSpider.TestDataParser.SuccessfulUrls);
        Assert.Contains("https://testsite.com/about.html", RobotsTxtIntegrationTestSpider.TestDataParser.SuccessfulUrls);
        
        Assert.Contains("https://testsite.com/admin/panel.html", RobotsTxtIntegrationTestSpider.TestDataParser.BlockedUrls);
        Assert.Contains("https://testsite.com/private/data.html", RobotsTxtIntegrationTestSpider.TestDataParser.BlockedUrls);
        Assert.Contains("https://testsite.com/api/endpoint", RobotsTxtIntegrationTestSpider.TestDataParser.BlockedUrls);

        // Verify all URLs were attempted
        Assert.Equal(6, RobotsTxtIntegrationTestSpider.TestDataParser.ProcessedUrls.Count);
        
        // Verify correct number of successful vs blocked
        Assert.Equal(3, RobotsTxtIntegrationTestSpider.TestDataParser.SuccessfulUrls.Count);
        Assert.Equal(3, RobotsTxtIntegrationTestSpider.TestDataParser.BlockedUrls.Count);

        httpClient.Dispose();
    }

    [Fact]
    public async Task Integration_SpiderWithoutRobotsTxt_AllowsAllRequests()
    {
        // Arrange
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        
        // Setup robots.txt not found
        SetupHttpNotFound(mockHttpHandler, "https://testsite.com/robots.txt");
        
        // Setup page responses for all URLs
        SetupHttpResponse(mockHttpHandler, "https://testsite.com/public/page1.html", 
            "<html><head><title>Public Page 1</title></head><body>Content</body></html>");
        SetupHttpResponse(mockHttpHandler, "https://testsite.com/admin/panel.html", 
            "<html><head><title>Admin Panel</title></head><body>Admin content</body></html>");

        var httpClient = new HttpClient(mockHttpHandler.Object);
        
        var builder = Builder.CreateDefaultBuilder<RobotsTxtIntegrationTestSpider>(options =>
        {
            options.Speed = 10;
            options.EmptySleepTime = 1;
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IHttpClientFactory>(provider =>
            {
                var factory = new Mock<IHttpClientFactory>();
                factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);
                return factory.Object;
            });
            
            // Add essential missing services
            services.AddSingleton<DotnetSpider.Proxy.IProxyService, DotnetSpider.Proxy.EmptyProxyService>();
        });

        builder.UseRobotsTxt();

        using var host = builder.Build();
        var spider = host.Services.GetServices<IHostedService>()
            .OfType<RobotsTxtIntegrationTestSpider>()
            .FirstOrDefault();

        // Act
        var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
        try
        {
            await host.StartAsync(cancellationTokenSource.Token);
            await Task.Delay(2000, cancellationTokenSource.Token);
            await host.StopAsync(cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected timeout
        }

        // Assert
        Assert.NotNull(spider);
        
        // Since robots.txt is not found, all requests should be allowed
        Assert.True(RobotsTxtIntegrationTestSpider.TestDataParser.SuccessfulUrls.Count > 0, "Some URLs should have been successfully crawled");
        Assert.Empty(RobotsTxtIntegrationTestSpider.TestDataParser.BlockedUrls);

        httpClient.Dispose();
    }

    private static void SetupHttpResponse(Mock<HttpMessageHandler> mockHandler, string url, string content)
    {
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString() == url),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(content),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, url)
            });
    }

    private static void SetupHttpNotFound(Mock<HttpMessageHandler> mockHandler, string url)
    {
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString() == url),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, url)
            });
    }
}