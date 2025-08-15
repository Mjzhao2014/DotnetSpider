using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DotnetSpider.Robots;

/// <summary>
/// Simple representation and evaluation logic for a robots.txt file.
/// Groups of user-agents and associated directives are parsed in a simplistic
/// fashion per RFC9309. Matching and path evaluation uses simple case-insensitive
/// prefix comparisons per the standard.
/// </summary>
public class RobotsTxt
{
    private readonly List<RobotsTxtGroup> _groups = new();

    public IReadOnlyList<RobotsTxtGroup> Groups => _groups;

    private RobotsTxt()
    {
    }

    /// <summary>
    /// Parse robots.txt content into a RobotsTxt instance. If parsing fails or no groups,
    /// returns an empty RobotsTxt (allow all).
    /// </summary>
    /// <param name="content">Contents of robots.txt</param>
    /// <returns></returns>
    public static RobotsTxt Parse(string content)
    {
        var robots = new RobotsTxt();
        if (string.IsNullOrWhiteSpace(content))
        {
            return robots;
        }

        RobotsTxtGroup currentGroup = null;
        void BeginGroup()
        {
            currentGroup = new RobotsTxtGroup();
            robots._groups.Add(currentGroup);
        }
        var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line))
            {
                currentGroup = null;
                continue;
            }
            // strip comments starting with '#'
            var hashIndex = line.IndexOf('#');
            if (hashIndex >= 0)
            {
                line = line.Substring(0, hashIndex).Trim();
            }
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }
            var field = line.Substring(0, colonIndex).Trim().ToLowerInvariant();
            var value = line.Substring(colonIndex + 1).Trim();
            switch (field)
            {
                case "user-agent":
                    if (currentGroup == null || currentGroup.HasDirectives)
                    {
                        BeginGroup();
                    }
                    currentGroup.UserAgents.Add(value);
                    break;
                case "disallow":
                    if (currentGroup == null)
                    {
                        BeginGroup();
                    }
                    currentGroup.Disallows.Add(value);
                    currentGroup.HasDirectives = true;
                    break;
                case "allow":
                    if (currentGroup == null)
                    {
                        BeginGroup();
                    }
                    currentGroup.Allows.Add(value);
                    currentGroup.HasDirectives = true;
                    break;
                case "crawl-delay":
                    if (currentGroup == null)
                    {
                        BeginGroup();
                    }
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    {
                        currentGroup.CrawlDelaySeconds = seconds;
                    }
                    currentGroup.HasDirectives = true;
                    break;
                default:
                    break;
            }
        }
        return robots;
    }

    /// <summary>
    /// Determine if the given path is allowed to be fetched by a user-agent string.
    /// If no matching group or no rules, returns true.
    /// </summary>
    public bool IsAllowed(string userAgent, string path)
    {
        var group = FindGroup(userAgent);
        if (group == null)
        {
            return true;
        }
        // Normalize path and rule patterns as lower-case
        var lowerPath = path ?? string.Empty;
        lowerPath = lowerPath.ToLowerInvariant();
        // Rules: find longest matching prefix among allow/disallow
        var bestLength = -1;
        var bestIsAllow = true;
        // evaluate allows first so that an allow at a given length wins over a disallow of same length
        foreach (var pattern in group.Allows)
        {
            var p = (pattern ?? string.Empty).ToLowerInvariant();
            if (lowerPath.StartsWith(p))
            {
                var len = p.Length;
                if (len > bestLength)
                {
                    bestLength = len;
                    bestIsAllow = true;
                }
                else if (len == bestLength && !bestIsAllow)
                {
                    // allow overrides disallow of same length
                    bestIsAllow = true;
                }
            }
        }
        // evaluate disallows
        foreach (var pattern in group.Disallows)
        {
            var p = (pattern ?? string.Empty).ToLowerInvariant();
            if (lowerPath.StartsWith(p))
            {
                var len = p.Length;
                if (len > bestLength)
                {
                    bestLength = len;
                    bestIsAllow = false;
                }
                else if (len == bestLength && bestLength > 0)
                {
                    // If tie, keep bestIsAllow as is (allow wins if previously set)
                }
            }
        }
        if (bestLength < 0)
        {
            return true;
        }
        return bestIsAllow;
    }

    /// <summary>
    /// Resolve crawl-delay seconds for the given user-agent if any.
    /// </summary>
    public double? GetCrawlDelay(string userAgent)
    {
        var group = FindGroup(userAgent);
        return group?.CrawlDelaySeconds;
    }

    private RobotsTxtGroup FindGroup(string userAgent)
    {
        if (_groups.Count == 0)
        {
            return null;
        }
        var ua = userAgent.ToLowerInvariant();
        RobotsTxtGroup wildcardGroup = null;
        RobotsTxtGroup bestGroup = null;
        var bestLen = -1;
        foreach (var group in _groups)
        {
            foreach (var pattern in group.UserAgents)
            {
                var matchPattern = pattern?.ToLowerInvariant() ?? string.Empty;
                if (matchPattern == "*")
                {
                    wildcardGroup = group;
                    continue;
                }
                if (ua.StartsWith(matchPattern))
                {
                    if (matchPattern.Length > bestLen)
                    {
                        bestLen = matchPattern.Length;
                        bestGroup = group;
                    }
                }
            }
        }
        if (bestGroup != null)
        {
            return bestGroup;
        }
        return wildcardGroup;
    }
}

public class RobotsTxtGroup
{
    public List<string> UserAgents { get; } = new();
    public List<string> Allows { get; } = new();
    public List<string> Disallows { get; } = new();
    public double? CrawlDelaySeconds { get; set; }
    /// <summary>
    /// Marker to determine if directives other than User-agent have been seen in this group
    /// </summary>
    public bool HasDirectives { get; set; }
}
