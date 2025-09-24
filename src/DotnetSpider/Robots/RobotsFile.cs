using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DotnetSpider.Robots;

/// <summary>
/// Represents the parsed rules from a robots.txt file. A robots.txt file consists
/// of one or more groups. Each group consists of one or more User-agent lines
/// followed by Allow/Disallow and optional Crawl-delay directives that apply to that group.
/// </summary>
public class RobotsFile
{
    public IList<RobotsGroup> Groups { get; } = new List<RobotsGroup>();

    public RobotsFile()
    {
    }

    /// <summary>
    /// Parses raw robots.txt content into a <see cref="RobotsFile"/>.
    /// </summary>
    public static RobotsFile Parse(string content)
    {
        var file = new RobotsFile();
        RobotsGroup? currentGroup = null;
        bool seenDirective = false;
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                // blank line resets grouping per RFC
                currentGroup = null;
                seenDirective = false;
                continue;
            }
            var commentIndex = trimmed.IndexOf('#');
            if (commentIndex >= 0)
            {
                trimmed = trimmed.Substring(0, commentIndex).Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
            }
            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            var directive = trimmed.Substring(0, separator).Trim().ToLowerInvariant();
            var value = trimmed.Substring(separator + 1).Trim();
            switch (directive)
            {
                case "user-agent":
                    if (currentGroup == null || seenDirective)
                    {
                        currentGroup = new RobotsGroup();
                        file.Groups.Add(currentGroup);
                        seenDirective = false;
                    }
                    currentGroup.UserAgents.Add(value);
                    break;
                case "disallow":
                    if (currentGroup == null)
                    {
                        // directives before any user-agent are ignored
                        break;
                    }
                    seenDirective = true;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        currentGroup.Disallow.Add(value);
                    }
                    break;
                case "allow":
                    if (currentGroup == null)
                    {
                        break;
                    }
                    seenDirective = true;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        currentGroup.Allow.Add(value);
                    }
                    break;
                case "crawl-delay":
                    if (currentGroup == null)
                    {
                        break;
                    }
                    seenDirective = true;
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay))
                    {
                        currentGroup.CrawlDelay = delay;
                    }
                    break;
                default:
                    break;
            }
        }
        return file;
    }

    /// <summary>
    /// Determines which group applies to the given user-agent string. Finds all groups
    /// whose User-agent entries are prefixes of the crawler's user-agent and returns
    /// the one with the longest matching prefix. If no match is found, falls back to
    /// any group that contains '*'. If still none found, returns null indicating
    /// crawling is allowed.
    /// </summary>
    public RobotsGroup? GetMatchingGroup(string userAgent)
    {
        var ua = userAgent.ToLowerInvariant();
        RobotsGroup? best = null;
        var bestLength = -1;
        foreach (var group in Groups)
        {
            foreach (var agentPattern in group.UserAgents)
            {
                var pattern = agentPattern.ToLowerInvariant();
                if (pattern == "*")
                {
                    // consider wildcard only if nothing more specific found
                    if (best == null)
                    {
                        best = group;
                    }
                }
                else if (ua.StartsWith(pattern))
                {
                    if (pattern.Length > bestLength)
                    {
                        best = group;
                        bestLength = pattern.Length;
                    }
                }
            }
        }
        return best;
    }
}
