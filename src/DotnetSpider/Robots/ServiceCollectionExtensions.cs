using Microsoft.Extensions.DependencyInjection;

namespace DotnetSpider.Robots;

/// <summary>
/// Extensions to opt in to robots.txt handling within spiders.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static Builder UseRobotsTxt(this Builder builder)
    {
        builder.Properties["UseRobotsTxt"] = "true";
        // Register the robots.txt manager and set the option flag.
        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton<RobotsTxtManager>();
            services.Configure<SpiderOptions>(options => options.UseRobotsTxt = true);
        });
        return builder;
    }
}
