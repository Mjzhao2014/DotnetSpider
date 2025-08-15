using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DotnetSpider.Robots;

/// <summary>
/// Parsed representation of a robots.txt file. A robots.txt file consists of one or more
/// groups of user-agent lines followed by rules (Allow/Disallow/Crawl-delay).
/// </summary>
public class RobotsTxt
{
    public IReadOnlyList<RobotsTxtGroup> Groups => _groups;

    private readonly List<RobotsTxtGroup> _groups = new();

    public RobotsTxt()
    {
    }

    /// <summary>
    /// Returns the group whose user-agent rules best match the supplied crawler user agent string.
    /// Matching is case-insensitive and by prefix; the longest matching user-agent string applies.
    /// If no group matches and there is no group for '*', returns null.
    /// </summary>
    public RobotsTxtGroup? FindMostSpecificGroupForUA(string userAgent)
    {
        userAgent ??= string.Empty;
        var uaLower = userAgent.ToLowerInvariant();
        RobotsTxtGroup? bestGroup = null;
        var bestLength = -1;
        foreach (var group in _groups)
        {
            foreach (var agent in group.UserAgents)
            {
                if (string.IsNullOrEmpty(agent)) continue;
                if (agent == "*")
                {
                    // wildcard matches everything with length 0 for comparison
                    if (bestGroup == null && bestLength < 0)
                    {
                        bestGroup = group;
                        bestLength = 0;
                    }
                    continue;
                }
                if (uaLower.StartsWith(agent.ToLowerInvariant()))
                {
                    if (agent.Length > bestLength)
                    {
                        bestLength = agent.Length;
                        bestGroup = group;
                    }
                }
            }
        }

        return bestGroup;
    }

    /// <summary>
    /// Returns true if the supplied path should be allowed to be fetched by the supplied user-agent
    /// according to this robots.txt. If there is no matching group, allows by default.
    /// The path is the path component (and optional query) of the URL, starting with '/'.
    /// </summary>
    public bool IsAllowed(string path, string userAgent)
    {
        var group = FindMostSpecificGroupForUA(userAgent);
        if (group == null)
        {
            return true;
        }
        return group.IsAllowed(path);
    }

    /// <summary>
    /// Parses the robots.txt file content into RobotsTxt groups. Lines beginning with '#'
    /// and blank lines are ignored. Key/value pairs are separated by ':' with optional whitespace.
    /// </summary>
    public static RobotsTxt Parse(string content)
    {
        var robots = new RobotsTxt();
        RobotsTxtGroup? currentGroup = null;
        bool seenDirective = false;
        if (content == null)
        {
            return robots;
        }

        var lines = content.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                // blank line resets group accumulation if no directives seen
                seenDirective = false;
                continue;
            }
            if (line.StartsWith("#"))
            {
                continue;
            }
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }
            var field = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (field.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                // if we have encountered directives in the current group, start a new group
                if (currentGroup == null || seenDirective)
                {
                    currentGroup = new RobotsTxtGroup();
                    robots._groups.Add(currentGroup);
                    seenDirective = false;
                }
                currentGroup.UserAgents.Add(value);
            }
            else if (field.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
            {
                if (currentGroup == null)
                {
                    currentGroup = new RobotsTxtGroup();
                    robots._groups.Add(currentGroup);
                }
                currentGroup.Disallows.Add(value);
                seenDirective = true;
            }
            else if (field.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                if (currentGroup == null)
                {
                    currentGroup = new RobotsTxtGroup();
                    robots._groups.Add(currentGroup);
                }
                currentGroup.Allows.Add(value);
                seenDirective = true;
            }
            else if (field.Equals("Crawl-delay", StringComparison.OrdinalIgnoreCase))
            {
                if (currentGroup == null)
                {
                    currentGroup = new RobotsTxtGroup();
                    robots._groups.Add(currentGroup);
                }
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay))
                {
                    currentGroup.CrawlDelay = delay;
                }
                seenDirective = true;
            }
        }
        return robots;
    }
}

/// <summary>
/// Represents a group of directives that apply to one or more user-agent strings.
/// </summary>
public class RobotsTxtGroup
{
    public List<string> UserAgents { get; } = new();
    public List<string> Disallows { get; } = new();
    public List<string> Allows { get; } = new();
    public int? CrawlDelay { get; set; }

    /// <summary>
    /// Determines if the supplied path is allowed by this group's Allow/Disallow rules.
    /// The most specific directive (longest matching prefix) wins. If no disallow matches,
    /// path is allowed. If Disallow is empty string, everything is allowed.
    /// </summary>
    public bool IsAllowed(string path)
    {
        int disallowLen = -1;
        int allowLen = -1;
        foreach (var a in Allows)
        {
            if (string.IsNullOrEmpty(a)) continue;
            if (path.StartsWith(a, StringComparison.OrdinalIgnoreCase))
            {
                if (a.Length > allowLen)
                {
                    allowLen = a.Length;
                }
            }
        }
        foreach (var d in Disallows)
        {
            if (string.IsNullOrEmpty(d))
            {
                // an empty Disallow means allow all
                return true;
            }
            if (path.StartsWith(d, StringComparison.OrdinalIgnoreCase))
            {
                if (d.Length > disallowLen)
                {
                    disallowLen = d.Length;
                }
            }
        }
        if (disallowLen < 0)
        {
            return true;
        }
        return allowLen > disallowLen;
    }
}
