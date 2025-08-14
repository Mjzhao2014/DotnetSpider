using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetSpider.Robots;

/// <summary>
/// Default implementation of an <see cref="IRobotsTxtService"/> that retrieves and caches
/// robots.txt files and enforces Disallow and Crawl-delay directives.
/// </summary>
public class RobotsTxtService : IRobotsTxtService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RobotsTxtService> _logger;
    private readonly RobotsTxtOptions _options;
    private readonly ConcurrentDictionary<string, RobotsFile> _robotsCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRequestTimes = new();

    public RobotsTxtService(IHttpClientFactory httpClientFactory,
        IOptions<RobotsTxtOptions> options,
        ILogger<RobotsTxtService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Ensure the robots.txt for the given URI has been cached.
    /// </summary>
    public async Task EnsureRulesForUriAsync(Uri uri)
    {
        var host = uri.Host;
        if (_robotsCache.ContainsKey(host))
        {
            return;
        }
        await GetRobotsFileForHostAsync(uri);
    }

    /// <summary>
    /// Returns whether the uri is disallowed by robots.txt.
    /// </summary>
    public async Task<bool> IsAllowedAsync(Uri uri)
    {
        var file = await GetRobotsFileForHostAsync(uri);
        return file.IsAllowed(uri.AbsolutePath, _options.UserAgent);
    }

    /// <summary>
    /// Enforces any crawl-delay directive for the uri's host.
    /// If delay is configured and the last request time is within that window,
    /// waits for the remaining time.
    /// </summary>
    public async Task EnforceDelayAsync(Uri uri)
    {
        var file = await GetRobotsFileForHostAsync(uri);
        var delaySeconds = file.GetCrawlDelaySeconds(_options.UserAgent);
        if (delaySeconds <= 0)
        {
            return;
        }

        var host = uri.Host;
        var now = DateTime.UtcNow;
        if (_lastRequestTimes.TryGetValue(host, out var lastTime))
        {
            var elapsedSeconds = (now - lastTime).TotalSeconds;
            if (elapsedSeconds < delaySeconds)
            {
                var waitMillis = (int)((delaySeconds - elapsedSeconds) * 1000);
                if (waitMillis > 0)
                {
                    _logger.LogDebug("Respecting Crawl-delay {Delay}s for host {Host}", delaySeconds, host);
                    await Task.Delay(waitMillis);
                }
            }
        }
        _lastRequestTimes[host] = DateTime.UtcNow;
    }

    private async Task<RobotsFile> GetRobotsFileForHostAsync(Uri uri)
    {
        var host = uri.Host;
        if (_robotsCache.TryGetValue(host, out var existing))
        {
            return existing;
        }
        try
        {
            var robotsUri = new Uri($"{uri.Scheme}://{uri.Host}/robots.txt");
            var client = _httpClientFactory.CreateClient(host);
            var response = await client.GetAsync(robotsUri);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var file = RobotsFile.Parse(content);
                _robotsCache[host] = file;
                _logger.LogInformation("Fetched robots.txt for {Host}", host);
                return file;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to fetch robots.txt for {Host}", host);
        }
        var empty = RobotsFile.Empty;
        _robotsCache[host] = empty;
        return empty;
    }

    /// <summary>
    /// Represents a parsed robots.txt file.
    /// </summary>
    internal class RobotsFile
    {
        private readonly ConcurrentDictionary<string, List<string>> _disallow = new();
        private readonly ConcurrentDictionary<string, double?> _crawlDelay = new();

        public static RobotsFile Empty { get; } = new();

        public static RobotsFile Parse(string content)
        {
            var file = new RobotsFile();
            var lines = content.Split('\n');
            var currentAgents = new List<string>();
            bool inGroup = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (line.StartsWith("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }
                    var agent = parts[1].Trim();
                    if (inGroup)
                    {
                        // start new group
                        currentAgents.Clear();
                        inGroup = false;
                    }
                    currentAgents.Add(agent);
                }
                else if (line.StartsWith("Disallow", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }
                    var value = parts[1].Trim();
                    foreach (var agent in currentAgents)
                    {
                        var list = file._disallow.GetOrAdd(agent, _ => new());
                        if (!string.IsNullOrEmpty(value))
                        {
                            list.Add(value);
                        }
                    }
                    inGroup = true;
                }
                else if (line.StartsWith("Crawl-delay", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }
                    if (double.TryParse(parts[1].Trim(), out var delay))
                    {
                        foreach (var agent in currentAgents)
                        {
                            file._crawlDelay[agent] = delay;
                        }
                    }
                    inGroup = true;
                }
            }
            return file;
        }

        public bool IsAllowed(string path, string userAgent)
        {
            var ua = string.IsNullOrWhiteSpace(userAgent) ? "*" : userAgent;
            if (!_disallow.TryGetValue(ua, out var disallowed))
            {
                // fall back to *
                _disallow.TryGetValue("*", out disallowed);
            }
            if (disallowed == null || disallowed.Count == 0)
            {
                return true;
            }
            foreach (var rule in disallowed)
            {
                if (string.IsNullOrEmpty(rule))
                {
                    continue;
                }
                if (rule == "/")
                {
                    return false;
                }
                if (path.StartsWith(rule, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        public double GetCrawlDelaySeconds(string userAgent)
        {
            var ua = string.IsNullOrWhiteSpace(userAgent) ? "*" : userAgent;
            if (_crawlDelay.TryGetValue(ua, out var delay))
            {
                return delay ?? 0;
            }
            if (_crawlDelay.TryGetValue("*", out delay))
            {
                return delay ?? 0;
            }
            return 0;
        }
    }
}
