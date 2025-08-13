using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.DataFlow;
using DotnetSpider.DataFlow.Parser;
using DotnetSpider.Http;
using DotnetSpider.Selector;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace DotnetSpider.Sample.samples;

[DisplayName("RobotsCompliance")]
public class RobotsComplianceSpider(
    IOptions<SpiderOptions> options,
    DependenceServices services,
    ILogger<Spider> logger)
    : Spider(options, services, logger)
{
    public static async Task RunAsync()
    {
        var builder = Builder.CreateDefaultBuilder<RobotsComplianceSpider>(x =>
        {
            x.Speed = 2; // Slower speed to demonstrate crawl-delay compliance
            x.Depth = 2; // Limited depth for demonstration
        });
        
        // Enable robots.txt compliance
        builder.UseRobotsTxt();
        builder.UseSerilog();
        
        await builder.Build().RunAsync();
    }

    class RobotsAwareDataParser : DataParser
    {
        protected override Task ParseAsync(DataFlowContext context)
        {
            var url = context.Request.RequestUri?.ToString();
            var title = context.Selectable.XPath(".//title")?.Value?.Trim();
            var h1 = context.Selectable.XPath(".//h1")?.Value?.Trim();
            
            context.AddData("URL", url);
            context.AddData("Title", title);
            context.AddData("H1", h1);
            context.AddData("StatusCode", context.Response.StatusCode);
            
            return Task.CompletedTask;
        }

        public override Task InitializeAsync()
        {
            // Validate only HTTP/HTTPS URLs
            AddRequiredValidator("^https?://");
            
            // Add follow request querier to follow links automatically
            AddFollowRequestQuerier(Selectors.XPath(".//a[@href]"));
            
            return Task.CompletedTask;
        }
    }

    protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
    {
        // Add some popular websites to test robots.txt compliance
        await AddRequestsAsync(
            new Request("https://www.github.com/"),
            new Request("https://stackoverflow.com/"),
            new Request("https://www.wikipedia.org/")
        );
        
        AddDataFlow<RobotsAwareDataParser>();
        AddDataFlow<ConsoleStorage>();
    }
}