using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Http;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Robots;

public interface IRobotsTxtManager
{
    Task<bool> IsAllowedAsync(string url, string userAgent, CancellationToken cancellationToken = default);
    Task<TimeSpan?> GetCrawlDelayAsync(string url, string userAgent, CancellationToken cancellationToken = default);
}

public class RobotsTxtManager : IRobotsTxtManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RobotsTxtManager> _logger;
    private readonly ConcurrentDictionary<string, Task<RobotsTxt>> _cache = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, DateTime> _cacheTimestamps = new();

    public RobotsTxtManager(IHttpClientFactory httpClientFactory, ILogger<RobotsTxtManager> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsAllowedAsync(string url, string userAgent, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(url);
            var robotsTxt = await GetRobotsTxtAsync(uri.Host, cancellationToken);
            return robotsTxt.IsAllowed(userAgent, uri.AbsolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check robots.txt for {Url}, allowing by default", url);
            return true; // Allow by default if robots.txt check fails
        }
    }

    public async Task<TimeSpan?> GetCrawlDelayAsync(string url, string userAgent, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(url);
            var robotsTxt = await GetRobotsTxtAsync(uri.Host, cancellationToken);
            return robotsTxt.GetCrawlDelay(userAgent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get crawl-delay from robots.txt for {Url}", url);
            return null;
        }
    }

    private async Task<RobotsTxt> GetRobotsTxtAsync(string host, CancellationToken cancellationToken)
    {
        // Check if cache entry is expired
        if (_cacheTimestamps.TryGetValue(host, out var timestamp) && 
            DateTime.UtcNow - timestamp > _cacheExpiry)
        {
            _cache.TryRemove(host, out _);
            _cacheTimestamps.TryRemove(host, out _);
        }

        return await _cache.GetOrAdd(host, async h =>
        {
            _cacheTimestamps.TryAdd(h, DateTime.UtcNow);
            return await FetchRobotsTxtAsync(h, cancellationToken);
        });
    }

    private async Task<RobotsTxt> FetchRobotsTxtAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var robotsUrl = $"https://{host}/robots.txt";
            var httpClient = _httpClientFactory.CreateClient(host);
            
            _logger.LogDebug("Fetching robots.txt from {RobotsUrl}", robotsUrl);
            
            var response = await httpClient.GetAsync(robotsUrl, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Successfully fetched robots.txt from {RobotsUrl}", robotsUrl);
                return RobotsTxt.Parse(content);
            }
            else
            {
                _logger.LogDebug("Failed to fetch robots.txt from {RobotsUrl}, status: {StatusCode}", 
                    robotsUrl, response.StatusCode);
                return new RobotsTxt(); // Return empty robots.txt (allows everything)
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error fetching robots.txt from {Host}", host);
            return new RobotsTxt(); // Return empty robots.txt (allows everything)
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning(ex, "Timeout fetching robots.txt from {Host}", host);
            return new RobotsTxt(); // Return empty robots.txt (allows everything)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching robots.txt from {Host}", host);
            return new RobotsTxt(); // Return empty robots.txt (allows everything)
        }
    }
}

public static class RobotsTxtExtensions
{
    public static string GetUserAgentFromHeaders(this Request request)
    {
        if (request.Headers.TryGetValue("User-Agent", out var userAgent))
        {
            return userAgent;
        }
        
        // Return default User-Agent if not found
        return "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.149 Safari/537.36 Edg/80.0.361.69";
    }
}