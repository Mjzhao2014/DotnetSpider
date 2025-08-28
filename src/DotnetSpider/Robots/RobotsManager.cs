using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Robots;

/// <summary>
/// Manages retrieval and caching of robots.txt rulesets per host for a crawler.
/// Provides methods to determine if a given request is allowed, and to enforce crawl-delay between
/// consecutive requests to the same host.
/// </summary>
public class RobotsManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RobotsManager> _logger;
    private readonly ConcurrentDictionary<string, RobotsFile> _robotsCache = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequestTime = new();

    public RobotsManager(IHttpClientFactory httpClientFactory, ILogger<RobotsManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static string GetHostKey(Uri uri)
    {
        // Use host:port if non-default ports are present.
        return uri.IsDefaultPort ? uri.Host.ToLowerInvariant() : $"{uri.Host.ToLowerInvariant()}:{uri.Port}";
    }

    private async Task<RobotsFile> LoadRobotsFileAsync(Uri baseUri, string userAgent)
    {
        var hostKey = GetHostKey(baseUri);
        if (_robotsCache.TryGetValue(hostKey, out var robots))
        {
            return robots;
        }
        try
        {
            var robotsUrl = new Uri($"{baseUri.Scheme}://{baseUri.Host}{(baseUri.IsDefaultPort ? string.Empty : ":" + baseUri.Port)}/robots.txt");
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(robotsUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                robots = RobotsFile.Parse(content, userAgent);
            }
            else
            {
                // Missing robots.txt or inaccessible: allow all.
                robots = RobotsFile.AllowAll;
            }
        }
        catch (Exception e)
        {
            // Any error fetching robots.txt, assume full access.
            _logger.LogDebug(e, "Failed fetching robots.txt for {Host}", baseUri);
            robots = RobotsFile.AllowAll;
        }
        _robotsCache[hostKey] = robots;
        return robots;
    }

    /// <summary>
    /// Returns whether the given request is allowed to be fetched based on robots.txt rules.
    /// If no robots.txt is present or cannot be retrieved, returns true.
    /// </summary>
    public async Task<bool> IsAllowedAsync(Request request)
    {
        var uri = request.RequestUri;
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return true;
        }
        var baseUri = new Uri($"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}");
        var ua = request.Headers.UserAgent;
        var robots = await LoadRobotsFileAsync(baseUri, ua);
        return robots.IsAllowed(uri.PathAndQuery);
    }

    /// <summary>
    /// Ensures the crawl-delay specified in robots.txt for the host of this request, if any,
    /// has elapsed before continuing.
    /// Records the timestamp of this request for future delay calculations.
    /// </summary>
    public async Task EnforceDelayAsync(Request request)
    {
        var uri = request.RequestUri;
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return;
        }
        var baseUri = new Uri($"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}");
        var ua = request.Headers.UserAgent;
        var robots = await LoadRobotsFileAsync(baseUri, ua);
        if (robots.CrawlDelay.HasValue)
        {
            var hostKey = GetHostKey(baseUri);
            if (_lastRequestTime.TryGetValue(hostKey, out var lastTime))
            {
                var since = DateTimeOffset.UtcNow - lastTime;
                var delay = robots.CrawlDelay.Value - since;
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay);
                    }
                    catch (TaskCanceledException)
                    {
                        // ignore
                    }
                }
            }
            _lastRequestTime[hostKey] = DateTimeOffset.UtcNow;
        }
    }
}
