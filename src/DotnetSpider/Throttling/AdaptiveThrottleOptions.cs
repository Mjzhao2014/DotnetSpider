using System;

namespace DotnetSpider.Throttling;

public class AdaptiveThrottleOptions
{
    public bool EnableAdaptiveThrottling { get; set; } = true;
    
    public bool EnableConditionalRequests { get; set; } = false;
    
    public bool CacheResponseContent { get; set; } = false;
    
    public int MaxRetryAttempts { get; set; } = 3;
    
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);
    
    public double EwmaAlpha { get; set; } = 0.3;
    
    public int MinConcurrency { get; set; } = 1;
    
    public int MaxConcurrency { get; set; } = 10;
    
    public int MaxLatencyThresholdMs { get; set; } = 5000;
    
    public int MinLatencyThresholdMs { get; set; } = 100;
    
    public double ErrorRateThreshold { get; set; } = 0.1;
    
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(30);
    
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.FromMilliseconds(100);
    
    public int MaxBandwidthBytesPerSecond { get; set; } = 0;
    
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(1);
    
    public int MaxCacheEntries { get; set; } = 1000;
}