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
        var matches = new List<(bool Allow, int Length, string Pattern)>();

        foreach (var pattern in Allow)
        {
            var length = GetMatchLength(path, pattern);
            if (length >= 0)
            {
                matches.Add((true, length, pattern));
            }
        }

        foreach (var pattern in Disallow)
        {
            var length = GetMatchLength(path, pattern);
            if (length >= 0)
            {
                matches.Add((false, length, pattern));
            }
        }

        if (matches.Count == 0)
        {
            return true;
        }

        var bestMatch = matches
            .OrderByDescending(m => m.Length)
            .ThenByDescending(m => m.Allow)
            .First();

        return bestMatch.Allow;
    }

    private static int GetMatchLength(string path, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return -1;
        }

        var text = path.ToLowerInvariant();
        var pat = pattern.ToLowerInvariant();

        int textIndex = 0;
        int patternIndex = 0;
        int starPatternIndex = -1;
        int starTextIndex = 0;
        int lastMatchEnd = 0;

        while (textIndex < text.Length)
        {
            if (patternIndex < pat.Length && (pat[patternIndex] == '*' || pat[patternIndex] == text[textIndex]))
            {
                if (pat[patternIndex] == '*')
                {
                    starPatternIndex = patternIndex++;
                    starTextIndex = textIndex;
                    lastMatchEnd = textIndex;
                }
                else
                {
                    patternIndex++;
                    textIndex++;
                    lastMatchEnd = textIndex;
                }
            }
            else if (starPatternIndex != -1)
            {
                patternIndex = starPatternIndex + 1;
                starTextIndex++;
                textIndex = starTextIndex;
                lastMatchEnd = textIndex;
            }
            else
            {
                break;
            }
        }

        while (patternIndex < pat.Length && pat[patternIndex] == '*')
        {
            patternIndex++;
        }

        if (patternIndex != pat.Length)
        {
            return -1;
        }

        return lastMatchEnd;
    }
}
