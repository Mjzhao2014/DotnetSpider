using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.Proxy;
using DotnetSpider.Robots;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Downloader;

public class RobotsAwareHttpClientDownloader : IDownloader
{
    private readonly HttpClientDownloader _baseDownloader;
    private readonly IRobotsTxtManager _robotsTxtManager;
    private readonly ICrawlDelayManager _crawlDelayManager;
    private readonly ILogger<RobotsAwareHttpClientDownloader> _logger;

    public RobotsAwareHttpClientDownloader(
        IHttpClientFactory httpClientFactory,
        IProxyService proxyService,
        ILogger<RobotsAwareHttpClientDownloader> logger,
        IRobotsTxtManager robotsTxtManager,
        ICrawlDelayManager crawlDelayManager,
        ILogger<HttpClientDownloader> baseLogger)
    {
        _baseDownloader = new HttpClientDownloader(httpClientFactory, proxyService, baseLogger);
        _robotsTxtManager = robotsTxtManager ?? throw new ArgumentNullException(nameof(robotsTxtManager));
        _crawlDelayManager = crawlDelayManager ?? throw new ArgumentNullException(nameof(crawlDelayManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Response> DownloadAsync(Request request)
    {
        try
        {
            // Check robots.txt compliance before downloading
            var userAgent = request.GetUserAgentFromHeaders();
            var isAllowed = await _robotsTxtManager.IsAllowedAsync(request.RequestUri.ToString(), userAgent);
            
            if (!isAllowed)
            {
                _logger.LogWarning("Request to {RequestUri} blocked by robots.txt", request.RequestUri);
                return new Response
                {
                    RequestHash = request.Hash,
                    StatusCode = HttpStatusCode.Forbidden,
                    ReasonPhrase = "Blocked by robots.txt",
                    Version = HttpVersion.Version11
                };
            }

            // Get crawl-delay from robots.txt and apply it
            var crawlDelay = await _robotsTxtManager.GetCrawlDelayAsync(request.RequestUri.ToString(), userAgent);
            if (crawlDelay.HasValue && crawlDelay.Value > TimeSpan.Zero)
            {
                await _crawlDelayManager.ApplyCrawlDelayAsync(request.RequestUri.Host, crawlDelay.Value);
            }

            return await _baseDownloader.DownloadAsync(request);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error in robots-aware downloader for {RequestUri}", request.RequestUri);
            // Fall back to base implementation if robots.txt check fails
            return await _baseDownloader.DownloadAsync(request);
        }
    }
}