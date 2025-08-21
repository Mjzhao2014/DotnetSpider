using System;
using DotnetSpider.Downloader;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetSpider.Throttling;

public static class BuilderExtensions
{
    public static Builder UseAdaptiveThrottling(this Builder builder)
    {
        return UseAdaptiveThrottling(builder, new AdaptiveDownloaderOptions());
    }
    
    public static Builder UseAdaptiveThrottling(this Builder builder, AdaptiveDownloaderOptions options)
    {
        builder.ConfigureServices((context, services) =>
        {
            services.AddAdaptiveThrottling(options);
        });
        
        return builder;
    }
    
    public static Builder UseAdaptiveThrottling(this Builder builder, Action<AdaptiveDownloaderOptions> configure)
    {
        var options = new AdaptiveDownloaderOptions();
        configure(options);
        
        return UseAdaptiveThrottling(builder, options);
    }
    
    public static Builder UseIntelligentRateLimiting(this Builder builder)
    {
        return UseAdaptiveThrottling(builder, options =>
        {
            options.EnableAdaptiveThrottling = true;
            options.EnableBandwidthThrottling = false;
        });
    }
    
    public static Builder UseBandwidthThrottling(this Builder builder, long maxBytesPerSecondPerHost = 1048576)
    {
        return UseAdaptiveThrottling(builder, options =>
        {
            options.EnableAdaptiveThrottling = true;
            options.EnableBandwidthThrottling = true;
            options.MaxBandwidthPerHost = maxBytesPerSecondPerHost;
        });
    }
}