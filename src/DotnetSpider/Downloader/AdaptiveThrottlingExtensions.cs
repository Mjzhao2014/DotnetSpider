using System;
using System.Net.Http;
using DotnetSpider.Downloader;
using DotnetSpider.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

            // Register proxy service if not already registered
            services.TryAddSingleton<IProxyService, EmptyProxyService>();

            // Register adaptive throttling services with factory to provide unwrapped options
            services.AddSingleton<AdaptiveThrottleManager>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                return new AdaptiveThrottleManager(options.Value);
            });
            
            services.AddSingleton<IDownloader>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var proxyService = provider.GetRequiredService<IProxyService>();
                var logger = provider.GetRequiredService<ILogger<AdaptiveHttpClientDownloader>>();
                var throttleManager = provider.GetRequiredService<AdaptiveThrottleManager>();
                var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                
                return new AdaptiveHttpClientDownloader(
                    httpClientFactory,
                    proxyService, 
                    logger,
                    throttleManager,
                    options.Value);
            });
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

            // Register proxy service if not already registered
            services.TryAddSingleton<IProxyService, EmptyProxyService>();

            // Register adaptive throttling services with factory to provide unwrapped options
            services.AddSingleton<AdaptiveThrottleManager>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                return new AdaptiveThrottleManager(options.Value);
            });
            
            services.AddSingleton<IDownloader>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var proxyService = provider.GetRequiredService<IProxyService>();
                var logger = provider.GetRequiredService<ILogger<AdaptiveHttpClientDownloader>>();
                var throttleManager = provider.GetRequiredService<AdaptiveThrottleManager>();
                var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
                
                return new AdaptiveHttpClientDownloader(
                    httpClientFactory,
                    proxyService, 
                    logger,
                    throttleManager,
                    options.Value);
            });
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

        // Register proxy service if not already registered
        services.TryAddSingleton<IProxyService, EmptyProxyService>();

        services.AddSingleton<AdaptiveThrottleManager>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
            return new AdaptiveThrottleManager(options.Value);
        });
        
        services.AddSingleton<IDownloader>(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var proxyService = provider.GetRequiredService<IProxyService>();
            var logger = provider.GetRequiredService<ILogger<AdaptiveHttpClientDownloader>>();
            var throttleManager = provider.GetRequiredService<AdaptiveThrottleManager>();
            var options = provider.GetRequiredService<IOptions<AdaptiveThrottleOptions>>();
            
            return new AdaptiveHttpClientDownloader(
                httpClientFactory,
                proxyService, 
                logger,
                throttleManager,
                options.Value);
        });

        return services;
    }
}
