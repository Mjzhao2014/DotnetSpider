using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetSpider.Robots;

/// <summary>
/// Extensions for configuring robots.txt support on a spider builder.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enables robots.txt enforcement when running the spider. The robots.txt file of each
    /// target host will be fetched and parsed, and requests will be filtered/serialized
    /// according to Allow/Disallow and Crawl-delay rules. Also sets the SpiderOption UseRobotsTxt flag.
    /// </summary>
    public static Builder UseRobotsTxt(this Builder builder)
    {
        builder.ConfigureServices(s =>
        {
            s.AddRobotsTxt();
            // Set options via configuration as well
            s.Configure<SpiderOptions>(opts => opts.UseRobotsTxt = true);
        });
        return builder;
    }

    public static IServiceCollection AddRobotsTxt(this IServiceCollection services)
    {
        services.AddSingleton<IRobotsService, RobotsService>();
        return services;
    }
}
