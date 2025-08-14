using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DotnetSpider.Robots;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetSpider.Tests;

/// <summary>
/// Tests simple robots.txt parsing and enforcement logic.
/// </summary>
public class RobotsTxtTests
{
    private class TestHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        private readonly System.Net.Http.HttpClient _client;
        public TestHttpClientFactory(string robotsContent)
        {
            var handler = new TestHandler(robotsContent);
            _client = new System.Net.Http.HttpClient(handler);
        }
        public System.Net.Http.HttpClient CreateClient(string name) => _client;
        private class TestHandler : System.Net.Http.HttpMessageHandler
        {
            private readonly string _content;
            public TestHandler(string content)
            {
                _content = content;
            }
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(_content)
                };
                return Task.FromResult(response);
            }
        }
    }

    [Fact]
    public async Task Disallow_and_CrawlDelay_are_Enforced()
    {
        var robots = "User-agent: *\nDisallow: /private\nCrawl-delay: 1";
        var factory = new TestHttpClientFactory(robots);
        var options = Options.Create(new RobotsTxtOptions { UserAgent = "*" });
        var service = new RobotsTxtService(factory, options, NullLogger<RobotsTxtService>.Instance);
        var uriAllowed = new Uri("http://example.com/index.html");
        var uriDisallowed = new Uri("http://example.com/private/page.html");
        var allowed = await service.IsAllowedAsync(uriAllowed);
        var disallowed = await service.IsAllowedAsync(uriDisallowed);
        Assert.True(allowed);
        Assert.False(disallowed);
        // measure whether crawl-delay is respected
        var sw = Stopwatch.StartNew();
        await service.EnforceDelayAsync(uriAllowed);
        await service.EnforceDelayAsync(uriAllowed);
        sw.Stop();
        // should have waited at least ~1 second between calls
        Assert.True(sw.ElapsedMilliseconds >= 1000);
    }
}
