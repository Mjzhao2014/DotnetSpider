using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Robots;

/// <summary>
/// Implementation of <see cref="IRobotsService"/> that fetches, parses and caches
/// robots.txt files per host. When enabled, before requests are issued the spider
/// will consult this service to determine if the request URI is allowed and wait
/// for any host-specific crawl-delays.
/// </summary>
public class RobotsService : IRobotsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RobotsService> _logger;
    private sealed class RobotsCacheEntry
    {
        public RobotsCacheEntry(RobotsFile file)
        {
            File = file;
        }

        public RobotsFile File { get; }
    }

    private readonly ConcurrentDictionary<string, RobotsCacheEntry> _robotsCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAccess = new();

    /// <summary>
    /// Default user agent used when no User-Agent header is set on the request.
    /// Must match default in <see cref="Request.ToHttpRequestMessage"/>.
    /// </summary>
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.149 Safari/537.36 Edg/80.0.361.69";

    public RobotsService(IHttpClientFactory httpClientFactory, ILogger<RobotsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string GetKeyForUri(Uri uri)
    {
        var builder = uri.Scheme + "://" + uri.Host;
        if (!uri.IsDefaultPort)
        {
            builder += ":" + uri.Port;
        }
        return builder;
    }

    private async Task<RobotsFile?> GetRobotsFileAsync(Uri uri)
    {
        var key = GetKeyForUri(uri);
        if (_robotsCache.TryGetValue(key, out var cached))
        {
            return cached.File;
        }
        try
        {
            // build robots.txt URI from scheme://host[:port]/robots.txt
            var baseAddress = new Uri(key);
            var robotsUri = new Uri(baseAddress, "/robots.txt");
            // Use named client per host as HttpClientDownloader does to conserve connections.
            var client = _httpClientFactory.CreateClient(uri.Host);
            var response = await client.GetAsync(robotsUri);
            if (!response.IsSuccessStatusCode)
            {
                // treat as no restrictions
                _robotsCache[key] = new RobotsCacheEntry(null);
                return null;
            }
            var content = await response.Content.ReadAsStringAsync();
            var file = RobotsFile.Parse(content);
            _robotsCache[key] = new RobotsCacheEntry(file);
            return file;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed fetching robots.txt for {Host}", uri.Host);
            // treat as no restrictions
            _robotsCache[key] = new RobotsCacheEntry(null);
            return null;
        }
    }

    private static string GetUserAgent(Request request)
    {
        var ua = request.Headers.UserAgent;
        if (string.IsNullOrWhiteSpace(ua))
        {
            // use default used by Request.ToHttpRequestMessage
            ua = DefaultUserAgent;
        }
        System.Console.WriteLine($"Resolved UA '{ua}' for {request.RequestUri}");
        return ua;
    }

    public async Task<bool> IsAllowedAsync(Request request)
    {
        var robots = await GetRobotsFileAsync(request.RequestUri);
        if (robots == null)
        {
            return true;
        }
        var group = robots.GetMatchingGroup(GetUserAgent(request));
        if (group == null)
        {
            return true;
        }
        var allowed = group.IsAllowed(request.RequestUri.PathAndQuery);
        System.Console.WriteLine($"IsAllowed for {request.RequestUri.PathAndQuery} with UA {GetUserAgent(request)} -> {allowed}");
        return allowed;
    }

    public async Task WaitForDelayAsync(Request request)
    {
        var robots = await GetRobotsFileAsync(request.RequestUri);
        if (robots == null)
        {
            return;
        }
        var group = robots.GetMatchingGroup(GetUserAgent(request));
        if (group == null)
        {
            return;
        }
        if (!group.CrawlDelay.HasValue || group.CrawlDelay.Value <= 0)
        {
            return;
        }
        var key = GetKeyForUri(request.RequestUri);
        var delay = TimeSpan.FromSeconds(group.CrawlDelay.Value);
        if (_lastAccess.TryGetValue(key, out var lastTime))
        {
            var since = DateTime.UtcNow - lastTime;
            if (since < delay)
            {
                var toWait = delay - since;
                await Task.Delay(toWait);
            }
        }
        _lastAccess[key] = DateTime.UtcNow;
    }
}
