using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Robots;

/// <summary>
/// Service responsible for retrieving and caching robots.txt per host, and enforcing
/// crawl-delay constraints between requests.
/// </summary>
public class RobotsTxtManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RobotsTxtManager> _logger;
    private readonly ConcurrentDictionary<string, RobotsTxt?> _robotsCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRequestTimes = new();

    public RobotsTxtManager(IHttpClientFactory httpClientFactory,
        ILogger<RobotsTxtManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private async Task<RobotsTxt?> FetchRobotsTxtAsync(Uri baseUri)
    {
        var hostKey = baseUri.Host.ToLowerInvariant();
        if (_robotsCache.TryGetValue(hostKey, out var existing))
        {
            return existing;
        }
        try
        {
            var robotsUri = new Uri($"{baseUri.Scheme}://{baseUri.Host}/robots.txt");
            var client = _httpClientFactory.CreateClient(baseUri.Host);
            var response = await client.GetAsync(robotsUri);
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                var parsed = RobotsTxt.Parse(text);
                _robotsCache[hostKey] = parsed;
                return parsed;
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Failed to fetch robots.txt for {Host}", baseUri.Host);
        }
        _robotsCache[hostKey] = null;
        return null;
    }

    /// <summary>
    /// Returns whether the given request is allowed to be fetched for its target host.
    /// If the host's robots.txt cannot be retrieved, full access is assumed.
    /// </summary>
    public async Task<bool> IsAllowedAsync(Request request)
    {
        var uri = request.RequestUri;
        if (uri == null)
        {
            return true;
        }
        var robots = await FetchRobotsTxtAsync(uri);
        if (robots == null)
        {
            return true;
        }
        var path = uri.PathAndQuery;
        var ua = request.Headers?.UserAgent;
        return robots.IsAllowed(path, ua ?? string.Empty);
    }

    /// <summary>
    /// Returns the crawl-delay in seconds for the given request's host if specified.
    /// Returns null if no crawl-delay directive present.
    /// </summary>
    public async Task<int?> GetCrawlDelayAsync(Request request)
    {
        var uri = request.RequestUri;
        if (uri == null)
        {
            return null;
        }
        var robots = await FetchRobotsTxtAsync(uri);
        if (robots == null)
        {
            return null;
        }
        var group = robots.FindMostSpecificGroupForUA(request.Headers?.UserAgent ?? string.Empty);
        return group?.CrawlDelay;
    }

    /// <summary>
    /// If the host associated with this request specifies a Crawl-delay directive, waits
    /// until enough time has elapsed since the previous request to this host.
    /// </summary>
    public async Task EnforceDelayAsync(Request request)
    {
        var delay = await GetCrawlDelayAsync(request);
        if (delay.HasValue && delay.Value > 0)
        {
            var hostKey = request.RequestUri.Host.ToLowerInvariant();
            if (_lastRequestTimes.TryGetValue(hostKey, out var lastTime))
            {
                var elapsed = DateTime.UtcNow - lastTime;
                var waitSeconds = delay.Value - elapsed.TotalSeconds;
                if (waitSeconds > 0)
                {
                    var ms = (int)(waitSeconds * 1000);
                    await Task.Delay(ms);
                }
            }
            _lastRequestTimes[hostKey] = DateTime.UtcNow;
        }
    }
}
