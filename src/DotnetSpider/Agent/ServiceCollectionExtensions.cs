using System;
using DotnetSpider.Downloader;
using DotnetSpider.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotnetSpider.Agent;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentHostService(this IServiceCollection services,
        Action<AgentOptions> configure = null)
    {
        services.AddHttpClient();

        if (configure != null)
        {
            services.Configure(configure);
        }

        // Register the adaptive throttle components
        services.AddSingleton<AdaptiveThrottleOptions>();
        services.AddSingleton<AdaptiveThrottleManager>();

        // 注册下载器
        // Register the adaptive HttpClientDownloader alongside the default implementation.
        services.AddDownloader<HttpClientDownloader>();
        services.AddDownloader<AdaptiveHttpClientDownloader>();
        services.AddDownloader<FileDownloader>();
        services.AddDownloader<EmptyDownloader>();
        services.AddDownloader<FakeHttpClientDownloader>();
        services.AddDownloader<PPPoEHttpClientDownloader>();
        services.TryAddSingleton<IProxyService, EmptyProxyService>();
        services.AddHostedService<AgentHostService>();
        return services;
    }
}
