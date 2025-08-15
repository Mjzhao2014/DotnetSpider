using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Robots;

/// <summary>
/// Robots.txt cache and fetcher. Maintains a cache of robots.txt per host.
/// </summary>
public class RobotsTxtManager : IRobotsTxtManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RobotsTxtManager> _logger;
    private readonly ConcurrentDictionary<string, RobotsTxt> _cache = new();

    public RobotsTxtManager(IHttpClientFactory httpClientFactory, ILogger<RobotsTxtManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string GetHostKey(Uri uri)
    {
        return uri.Host.ToLowerInvariant();
    }

    /// <summary>
    /// Attempt to fetch /robots.txt for the given request URI.
    /// Returns null if the file is missing or cannot be retrieved.
    /// </summary>
    private async Task<RobotsTxt> FetchRobotsAsync(Uri requestUri)
    {
        try
        {
            var baseUri = new Uri($"{requestUri.Scheme}://{requestUri.Host}");
            var robotsUri = new Uri(baseUri, "/robots.txt");
            var client = _httpClientFactory.CreateClient(requestUri.Host);
            var response = await client.GetAsync(robotsUri);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var content = await response.Content.ReadAsStringAsync();
            var robots = RobotsTxt.Parse(content);
            return robots;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to fetch robots.txt for {Host}", requestUri.Host);
            return null;
        }
    }

    public async Task<RobotsTxt> GetRobotsTxtAsync(Uri uri)
    {
        var key = GetHostKey(uri);
        if (_cache.TryGetValue(key, out var robots))
        {
            return robots;
        }
        robots = await FetchRobotsAsync(uri);
        _cache.TryAdd(key, robots);
        return robots;
    }
}

public interface IRobotsTxtManager
{
    /// <summary>
    /// Returns robots directives for the host of the provided URI.
    /// May return null if file missing or failed retrieving.
    /// </summary>
    Task<RobotsTxt> GetRobotsTxtAsync(Uri uri);
}
