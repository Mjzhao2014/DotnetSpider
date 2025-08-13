using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DotnetSpider.Robots;

public class RobotsTxt
{
    private readonly List<UserAgentGroup> _userAgentGroups = new();
    
    public TimeSpan? CrawlDelay { get; private set; }
    public string Host { get; private set; }
    
    public static RobotsTxt Parse(string content)
    {
        var robotsTxt = new RobotsTxt();
        if (string.IsNullOrWhiteSpace(content))
        {
            return robotsTxt;
        }

        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        UserAgentGroup currentGroup = null;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            // Skip empty lines and comments
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                continue;

            var colonIndex = trimmedLine.IndexOf(':');
            if (colonIndex == -1)
                continue;

            var directive = trimmedLine.Substring(0, colonIndex).Trim().ToLowerInvariant();
            var value = trimmedLine.Substring(colonIndex + 1).Trim();

            switch (directive)
            {
                case "user-agent":
                    currentGroup = new UserAgentGroup(value);
                    robotsTxt._userAgentGroups.Add(currentGroup);
                    break;
                
                case "disallow":
                    currentGroup?.DisallowedPaths.Add(value);
                    break;
                
                case "allow":
                    currentGroup?.AllowedPaths.Add(value);
                    break;
                
                case "crawl-delay":
                    if (currentGroup != null && double.TryParse(value, out var delay))
                    {
                        currentGroup.CrawlDelay = TimeSpan.FromSeconds(delay);
                    }
                    break;
                
                case "host":
                    robotsTxt.Host = value;
                    break;
            }
        }

        return robotsTxt;
    }

    public bool IsAllowed(string userAgent, string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        var applicableGroups = GetApplicableGroups(userAgent);
        
        // If no applicable groups found, allow by default
        if (!applicableGroups.Any())
            return true;

        foreach (var group in applicableGroups)
        {
            // Check Allow rules first (more specific)
            foreach (var allowedPath in group.AllowedPaths)
            {
                if (PathMatches(path, allowedPath))
                    return true;
            }

            // Check Disallow rules
            foreach (var disallowedPath in group.DisallowedPaths)
            {
                if (PathMatches(path, disallowedPath))
                    return false;
            }
        }

        return true;
    }

    public TimeSpan? GetCrawlDelay(string userAgent)
    {
        var applicableGroups = GetApplicableGroups(userAgent);
        
        // Return the first crawl-delay found in applicable groups
        foreach (var group in applicableGroups)
        {
            if (group.CrawlDelay.HasValue)
                return group.CrawlDelay.Value;
        }

        return CrawlDelay;
    }

    private List<UserAgentGroup> GetApplicableGroups(string userAgent)
    {
        var result = new List<UserAgentGroup>();
        var wildcardGroups = new List<UserAgentGroup>();
        
        foreach (var group in _userAgentGroups)
        {
            if (group.MatchesUserAgent(userAgent))
            {
                if (group.UserAgent == "*")
                {
                    wildcardGroups.Add(group);
                }
                else
                {
                    result.Add(group);
                }
            }
        }

        // If we have specific matches, only use those; otherwise use wildcard matches
        if (result.Count > 0)
        {
            return result;
        }
        
        return wildcardGroups;
    }

    private static bool PathMatches(string path, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        // Empty pattern disallows everything
        if (pattern == "")
            return true;

        // Root path special case
        if (pattern == "/")
            return path == "/";

        // Convert robots.txt pattern to regex pattern
        var regexPattern = ConvertToRegexPattern(pattern);
        
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(path, regexPattern);
        }
        catch
        {
            // If regex fails, fall back to simple prefix matching
            return path.StartsWith(pattern.TrimEnd('$'));
        }
    }

    private static string ConvertToRegexPattern(string robotsPattern)
    {
        var regexPattern = System.Text.RegularExpressions.Regex.Escape(robotsPattern);
        
        // Handle wildcards - unescape * and convert to .*
        regexPattern = regexPattern.Replace("\\*", ".*");
        
        // Handle end anchor
        if (regexPattern.EndsWith("\\$"))
        {
            regexPattern = regexPattern.Substring(0, regexPattern.Length - 2) + "$";
        }
        else if (!regexPattern.EndsWith("$"))
        {
            // If no explicit end anchor, match as prefix (add .* at the end)
            regexPattern = "^" + regexPattern;
        }
        else
        {
            regexPattern = "^" + regexPattern;
        }
        
        return regexPattern;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        var patternIndex = 0;
        var inputIndex = 0;
        var starIndex = -1;
        var match = 0;

        while (inputIndex < input.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                match = inputIndex;
            }
            else if (patternIndex < pattern.Length && 
                     (pattern[patternIndex] == input[inputIndex] || pattern[patternIndex] == '?'))
            {
                patternIndex++;
                inputIndex++;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                inputIndex = ++match;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            patternIndex++;

        return patternIndex == pattern.Length;
    }

    private class UserAgentGroup
    {
        public string UserAgent { get; }
        public List<string> DisallowedPaths { get; } = new();
        public List<string> AllowedPaths { get; } = new();
        public TimeSpan? CrawlDelay { get; set; }

        public UserAgentGroup(string userAgent)
        {
            UserAgent = userAgent ?? throw new ArgumentNullException(nameof(userAgent));
        }

        public bool MatchesUserAgent(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent))
                return UserAgent == "*";

            return UserAgent == "*" || 
                   userAgent.Contains(UserAgent, StringComparison.OrdinalIgnoreCase);
        }
    }
}