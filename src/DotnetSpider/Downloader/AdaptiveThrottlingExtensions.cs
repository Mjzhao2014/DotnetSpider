using System;
using DotnetSpider.Downloader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetSpider.Downloader;

/// <summary>
/// Extension methods for configuring adaptive throttling in the Builder
/// </summary>
public static class AdaptiveThrottlingExtensions
{
    /// <summary>
    /// Configures the Builder to use adaptive throttling with the AdaptiveHttpClientDownloader
    /// </summary>
    /// <param name="builder">The Builder instance</param>
    /// <param name="configure">Configuration action for AdaptiveThrottleOptions</param>
    /// <returns>The Builder instance for chaining</returns>
    public static Builder UseAdaptiveThrottling(this Builder builder, Action<AdaptiveThrottleOptions> configure = null)
    {
        builder.ConfigureServices(services =>
        {
            // Configure adaptive throttle options
            if (configure != null)
            {
                services.Configure(configure);
            }

            // Register adaptive throttling services
            services.AddSingleton<AdaptiveThrottleManager>();
            services.AddSingleton<IDownloader, AdaptiveHttpClientDownloader>();
        });

        return builder;
    }

    /// <summary>
    /// Configures the Builder to use adaptive throttling with context-aware configuration
    /// </summary>
    /// <param name="builder">The Builder instance</param>
    /// <param name="configure">Configuration action with HostBuilderContext and AdaptiveThrottleOptions</param>
    /// <returns>The Builder instance for chaining</returns>
    public static Builder UseAdaptiveThrottling(this Builder builder, Action<HostBuilderContext, AdaptiveThrottleOptions> configure)
    {
        builder.ConfigureServices((context, services) =>
        {
            // Configure adaptive throttle options with context
            services.Configure<AdaptiveThrottleOptions>(options => configure(context, options));

            // Register adaptive throttling services
            services.AddSingleton<AdaptiveThrottleManager>();
            services.AddSingleton<IDownloader, AdaptiveHttpClientDownloader>();
        });

        return builder;
    }

    /// <summary>
    /// Adds adaptive throttling services to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration action for AdaptiveThrottleOptions</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddAdaptiveThrottling(this IServiceCollection services, Action<AdaptiveThrottleOptions> configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddSingleton<AdaptiveThrottleManager>();
        services.AddSingleton<IDownloader, AdaptiveHttpClientDownloader>();

        return services;
    }
}
