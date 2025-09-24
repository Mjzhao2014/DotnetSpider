using System;
using System.Collections.Generic;
using System.Linq;

namespace DotnetSpider.Robots;

/// <summary>
/// Represents a group of rules in a robots.txt file. A group is composed of one or more
/// User-agent directives followed by any number of Allow, Disallow and optional Crawl-delay.
/// This class can be used to evaluate whether a given path is allowed based on the
/// directives in the group.
/// </summary>
public class RobotsGroup
{
    /// <summary>
    /// List of user-agent tokens that this group applies to.
    /// </summary>
    public IList<string> UserAgents { get; } = new List<string>();

    /// <summary>
    /// List of allowed path patterns.
    /// </summary>
    public IList<string> Allow { get; } = new List<string>();

    /// <summary>
    /// List of disallowed path patterns.
    /// </summary>
    public IList<string> Disallow { get; } = new List<string>();

    /// <summary>
    /// Optional crawl-delay governing delay in seconds between requests.
    /// </summary>
    public double? CrawlDelay { get; set; }

    /// <summary>
    /// Returns true if the provided path is allowed under this group's allow/disallow directives.
    /// If there are no matching directives, the path is allowed.
    /// When both allow and disallow patterns match, the pattern with the longest match length wins.
    /// </summary>
    /// <param name="path">URI path and query to evaluate.</param>
    public bool IsAllowed(string path)
    {
        // gather all matching directives and choose longest match
        var matches = new List<(bool Allow, string Pattern)>();
        foreach (var pattern in Allow)
        {
            if (Matches(path, pattern))
            {
                matches.Add((true, pattern));
            }
        }
        foreach (var pattern in Disallow)
        {
            if (Matches(path, pattern))
            {
                matches.Add((false, pattern));
            }
        }
        if (matches.Count == 0)
        {
            return true;
        }
        var longest = matches.OrderByDescending(m => m.Pattern.Length).First();
        return longest.Allow;
    }

    /// <summary>
    /// Checks whether a path matches a robots directive pattern. Patterns may include
    /// '*' wildcards which match any sequence of characters. Matching is case-insensitive.
    /// If the pattern does not contain any wildcards it is treated as a simple prefix match.
    /// </summary>
    private static bool Matches(string path, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }
        // normalize to lower-case for case-insensitive comparison
        var text = path.ToLowerInvariant();
        pattern = pattern.ToLowerInvariant();
        // simple prefix match if no wildcard present
        if (!pattern.Contains('*'))
        {
            return text.StartsWith(pattern);
        }
        return WildcardMatch(text, pattern);
    }

    /// <summary>
    /// Performs basic wildcard matching supporting '*' wildcard which matches 0 or more characters.
    /// </summary>
    private static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, match = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '*' || pattern[p] == text[t]))
            {
                if (pattern[p] == '*')
                {
                    star = p;
                    match = t;
                    p++;
                }
                else
                {
                    t++;
                    p++;
                }
            }
            else if (star != -1)
            {
                p = star + 1;
                match++;
                t = match;
            }
            else
            {
                return false;
            }
        }
        // consume trailing '*' in pattern
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }
}
