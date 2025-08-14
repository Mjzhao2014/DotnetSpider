using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetSpider.Robots;

/// <summary>
/// Extensions for enabling robots.txt support on a spider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enables robots.txt support on the builder and registers an <see cref="IRobotsTxtService"/>.
    /// </summary>
    public static Builder UseRobotsTxt(this Builder builder, Action<RobotsTxtOptions> configure = null)
    {
        builder.Properties["UseRobotsTxt"] = "true";
        builder.ConfigureServices(s =>
        {
            if (configure != null)
            {
                s.Configure(configure);
            }
            s.AddSingleton<IRobotsTxtService, RobotsTxtService>();
        });
        return builder;
    }
}
