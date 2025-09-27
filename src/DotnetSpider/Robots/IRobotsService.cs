using System.Threading.Tasks;
using DotnetSpider.Http;

namespace DotnetSpider.Robots;

/// <summary>
/// Service responsible for fetching, caching and evaluating robots.txt rules for hosts.
/// Allows consumers to check whether a given request URI is permitted and to honor
/// host-level crawl-delays defined in robots.txt files.
/// </summary>
public interface IRobotsService
{
    /// <summary>
    /// Returns true if the request's URI is allowed under the target host's robots.txt rules.
    /// If the robots file is missing or cannot be retrieved, this will return true.
    /// </summary>
    /// <param name="request">The request to check.</param>
    Task<bool> IsAllowedAsync(Request request);

    /// <summary>
    /// If the target host's robots.txt file defines a crawl-delay directive for this user-agent,
    /// waits until the minimum delay since the last request to that host has elapsed.
    /// If no crawl-delay is configured, returns immediately.
    /// This should be invoked before issuing a request to a host.
    /// </summary>
    /// <param name="request">Request about to be issued.</param>
    Task WaitForDelayAsync(Request request);
}
