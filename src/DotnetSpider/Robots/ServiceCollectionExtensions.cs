using DotnetSpider.Downloader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotnetSpider.Robots;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add robots.txt support to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddRobotsTxt(this IServiceCollection services)
    {
        services.TryAddSingleton<IRobotsTxtManager, RobotsTxtManager>();
        services.TryAddSingleton<ICrawlDelayManager, CrawlDelayManager>();
        return services;
    }

    /// <summary>
    /// Replace the default HttpClientDownloader with RobotsAwareHttpClientDownloader
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection UseRobotsAwareDownloader(this IServiceCollection services)
    {
        services.AddRobotsTxt();
        
        // Ensure we have required dependencies
        services.TryAddSingleton<DotnetSpider.Proxy.IProxyService, DotnetSpider.Proxy.EmptyProxyService>();
        
        // Replace the default downloader registration
        services.RemoveAll<IDownloader>();
        services.TryAddSingleton<IDownloader, RobotsAwareHttpClientDownloader>();
        
        return services;
    }
}