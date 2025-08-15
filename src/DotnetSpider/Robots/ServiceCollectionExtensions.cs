using Microsoft.Extensions.DependencyInjection;

namespace DotnetSpider.Robots;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enable robots.txt support for spiders. Adds a singleton robots.txt manager and
    /// configures spider options to respect robots directives.
    /// </summary>
    public static Builder UseRobotsTxt(this Builder builder)
    {
        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton<IRobotsTxtManager, RobotsTxtManager>();
        });
        builder.ConfigureServices((_, services) =>
        {
            services.Configure<SpiderOptions>(o => o.UseRobotsTxt = true);
        });
        return builder;
    }
}
