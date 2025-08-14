using System;
using System.Threading.Tasks;

namespace DotnetSpider.Robots;

/// <summary>
/// A service responsible for retrieving and evaluating robots.txt files per host.
/// </summary>
public interface IRobotsTxtService
{
    /// <summary>
    /// Ensures the robots.txt for the given URI's host has been retrieved and parsed.
    /// </summary>
    Task EnsureRulesForUriAsync(Uri uri);

    /// <summary>
    /// Returns whether the specified URI is allowed to be crawled under robots.txt rules.
    /// </summary>
    Task<bool> IsAllowedAsync(Uri uri);

    /// <summary>
    /// Blocks until any crawl-delay specified by robots.txt for the URI's host has elapsed
    /// since the last request. Updates the last-requested time for the host.
    /// </summary>
    Task EnforceDelayAsync(Uri uri);
}
