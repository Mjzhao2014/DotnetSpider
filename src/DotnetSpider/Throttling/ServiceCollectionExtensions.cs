using System;
using DotnetSpider.Downloader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotnetSpider.Throttling;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAdaptiveThrottling(this IServiceCollection services)
    {
        return AddAdaptiveThrottling(services, new AdaptiveDownloaderOptions());
    }
    
    public static IServiceCollection AddAdaptiveThrottling(this IServiceCollection services, 
        AdaptiveDownloaderOptions options)
    {
        services.TryAddSingleton<IHostThrottler, HostThrottler>();
        services.TryAddSingleton(options);
        
        services.Replace(ServiceDescriptor.Transient<IDownloader, AdaptiveHttpClientDownloader>());
        
        return services;
    }
    
    public static IServiceCollection AddAdaptiveThrottling(this IServiceCollection services,
        Action<AdaptiveDownloaderOptions> configure)
    {
        var options = new AdaptiveDownloaderOptions();
        configure(options);
        
        return AddAdaptiveThrottling(services, options);
    }
}