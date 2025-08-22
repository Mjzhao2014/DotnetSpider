using System;
using DotnetSpider.Downloader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotnetSpider.Throttling;

public static class ServiceCollectionExtensions
{
    public static Builder UseAdaptiveThrottling(this Builder builder)
    {
        return builder.UseAdaptiveThrottling(null);
    }

    public static Builder UseAdaptiveThrottling(this Builder builder, Action<AdaptiveThrottleOptions> configure)
    {
        builder.ConfigureServices((context, services) =>
        {
            var options = new AdaptiveThrottleOptions();
            configure?.Invoke(options);
            
            services.TryAddSingleton(options);
            services.TryAddSingleton<AdaptiveThrottleManager>();
            services.AddDownloader<AdaptiveHttpClientDownloader>();
        });

        return builder;
    }

    public static Builder UseIntelligentRateLimiting(this Builder builder)
    {
        return builder.UseAdaptiveThrottling(opts =>
        {
            opts.EnableAdaptiveThrottling = true;
            opts.EnableConditionalRequests = false;
            opts.CacheResponseContent = false;
        });
    }

    public static Builder UseConditionalRequests(this Builder builder)
    {
        return builder.UseAdaptiveThrottling(opts =>
        {
            opts.EnableAdaptiveThrottling = true;
            opts.EnableConditionalRequests = true;
            opts.CacheResponseContent = true;
        });
    }

    public static Builder UseBandwidthThrottling(this Builder builder, int maxBytesPerSecond = 1024 * 1024)
    {
        return builder.UseAdaptiveThrottling(opts =>
        {
            opts.EnableAdaptiveThrottling = true;
            opts.MaxBandwidthBytesPerSecond = maxBytesPerSecond;
        });
    }

    public static IServiceCollection AddAdaptiveThrottling(this IServiceCollection services)
    {
        return services.AddAdaptiveThrottling(null);
    }

    public static IServiceCollection AddAdaptiveThrottling(this IServiceCollection services, Action<AdaptiveThrottleOptions> configure)
    {
        var options = new AdaptiveThrottleOptions();
        configure?.Invoke(options);
        
        services.TryAddSingleton(options);
        services.TryAddSingleton<AdaptiveThrottleManager>();
        services.AddDownloader<AdaptiveHttpClientDownloader>();
        
        return services;
    }
}