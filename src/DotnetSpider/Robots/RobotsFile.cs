using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DotnetSpider.Robots;

/// <summary>
/// Represents the robots.txt rules applicable to a specific crawler user-agent.
/// Contains ordered allow/disallow rules and an optional crawl-delay.
/// </summary>
public class RobotsFile
{
    /// <summary>
    /// Collection of allow/disallow rules in the order defined in the robots.txt file.
    /// </summary>
    private readonly List<Rule> _rules;

    /// <summary>
    /// Optional crawl-delay directive (seconds) for this user-agent.
    /// </summary>
    public TimeSpan? CrawlDelay { get; }

    private RobotsFile(List<Rule> rules, TimeSpan? crawlDelay)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
    }

    /// <summary>
    /// Represents a robots file with no directives (allow all).
    /// </summary>
    internal static RobotsFile AllowAll { get; } = new(new List<Rule>(), null);

    /// <summary>
    /// Determines if the provided path is allowed for this user-agent based on the allow/disallow rules.
    /// The algorithm computes all matching allow/disallow rules and returns the most specific match.
    /// If no rule matches the path, it is allowed by default.
    /// </summary>
    /// <param name="path">The path component of the URL including query string.</param>
    /// <returns>True if the crawler is permitted to fetch the path, false if disallowed.</returns>
    public bool IsAllowed(string path)
    {
        if (_rules.Count == 0)
        {
            return true;
        }
        var bestRule = default(Rule);
        var bestLength = -1;
        foreach (var rule in _rules)
        {
            if (rule.IsMatch(path))
            {
                var length = rule.MatchLength;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestRule = rule;
                }
                else if (length == bestLength && bestRule != null && bestRule.Allow == false && rule.Allow)
                {
                    // For equal length matches, allow overrides disallow.
                    bestRule = rule;
                }
            }
        }
        if (bestRule == null)
        {
            return true;
        }
        return bestRule.Allow;
    }

    /// <summary>
    /// Parses the robots.txt content into a RobotsFile containing only the rules applicable to the specified user-agent.
    /// Groups the directives by user-agent prefix match and selects the group with the longest matching user-agent string.
    /// If no matching group is found and no User-agent: * group exists, an empty RobotsFile is returned to allow all crawling.
    /// </summary>
    /// <param name="content">Content of robots.txt file.</param>
    /// <param name="userAgent">Crawler's user agent string.</param>
    /// <returns>RobotsFile representing directives for this crawler.</returns>
    public static RobotsFile Parse(string content, string userAgent)
    {
        var groups = new List<Group>();
        Group currentGroup = null;
        var lines = content.Split('\n', '\r');
        bool seenDirective = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                // Blank line terminates current group.
                currentGroup = null;
                seenDirective = false;
                continue;
            }
            // Strip off any inline comments.
            var hashIndex = line.IndexOf('#');
            if (hashIndex >= 0)
            {
                line = line[..hashIndex].Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
            }
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }
            var field = line[..colonIndex].Trim().ToLowerInvariant();
            var value = line[(colonIndex + 1)..].Trim();
            switch (field)
            {
                case "user-agent":
                    // If we've already seen directives in this group and encounter a new user-agent,
                    // start a new group.
                    if (currentGroup == null || seenDirective)
                    {
                        currentGroup = new Group();
                        groups.Add(currentGroup);
                        seenDirective = false;
                    }
                    if (!string.IsNullOrEmpty(value))
                    {
                        currentGroup.UserAgents.Add(value.ToLowerInvariant());
                    }
                    break;
                case "allow":
                case "disallow":
                    if (currentGroup == null)
                    {
                        currentGroup = new Group();
                        groups.Add(currentGroup);
                    }
                    currentGroup.Rules.Add(new Rule(value, field == "allow"));
                    seenDirective = true;
                    break;
                case "crawl-delay":
                    if (currentGroup == null)
                    {
                        currentGroup = new Group();
                        groups.Add(currentGroup);
                    }
                    if (double.TryParse(value, out var delaySec))
                    {
                        currentGroup.CrawlDelay = TimeSpan.FromSeconds(delaySec);
                    }
                    seenDirective = true;
                    break;
                // ignore other directives (e.g., Sitemap)
            }
        }
        // Determine applicable group.
        var uaLower = (userAgent ?? string.Empty).ToLowerInvariant();
        Group bestGroup = null;
        var bestMatchLength = -1;
        foreach (var group in groups)
        {
            // Default group for wildcard if no explicit matches.
            foreach (var agent in group.UserAgents)
            {
                if (agent == "*")
                {
                    if (bestGroup == null)
                    {
                        bestGroup = group;
                    }
                }
                else if (!string.IsNullOrEmpty(uaLower) && uaLower.StartsWith(agent))
                {
                    var length = agent.Length;
                    if (length > bestMatchLength)
                    {
                        bestMatchLength = length;
                        bestGroup = group;
                    }
                }
            }
        }
        if (bestGroup == null)
        {
            // No matching group and no User-agent: * group.
            return AllowAll;
        }
        return new RobotsFile(bestGroup.Rules, bestGroup.CrawlDelay);
    }

    /// <summary>
    /// Represents a single allow/disallow directive.
    /// </summary>
    private class Rule
    {
        private readonly Regex _regex;
        public bool Allow { get; }
        public string Pattern { get; }
        public int MatchLength => Pattern?.Replace("*", string.Empty).Length ?? 0;

        public Rule(string pattern, bool allow)
        {
            Pattern = pattern ?? string.Empty;
            Allow = allow;
            if (string.IsNullOrEmpty(pattern))
            {
                _regex = null;
            }
            else if (pattern.Contains('*'))
            {
                // Convert simple glob-like * patterns to regex.
                var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*");
                _regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
        }

        public bool IsMatch(string path)
        {
            if (string.IsNullOrEmpty(Pattern))
            {
                // Empty pattern never restricts crawling.
                return false;
            }
            if (_regex != null)
            {
                return _regex.IsMatch(path);
            }
            return path.StartsWith(Pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Represents a group of directives associated with one or more user-agents.
    /// </summary>
    private class Group
    {
        public List<string> UserAgents { get; } = new();
        public List<Rule> Rules { get; } = new();
        public TimeSpan? CrawlDelay { get; set; }
    }
}
