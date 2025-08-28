using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetSpider.Robots;

/// <summary>
/// Extension methods to configure robots.txt support on a spider builder.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enables robots.txt compliance enforcement for the crawler.
    /// When enabled, the crawler will fetch and obey robots.txt directives for each host and respect any crawl-delay.
    /// </summary>
    public static Builder UseRobotsTxt(this Builder builder)
    {
        builder.Properties["UseRobotsTxt"] = "true";
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<RobotsManager>();
        });
        return builder;
    }
}
