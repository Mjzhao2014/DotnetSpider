using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Options to control adaptive throttling behavior for HTTP downloads.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Enables adaptive throttling. If disabled, the downloader will fall back to the base HttpClientDownloader behavior.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; } = true;

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum delay to back off between retry attempts when no Retry-After header is present.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Alpha parameter for EWMA latency smoothing. 0-1.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.3;

    /// <summary>
    /// Minimum concurrency limit per host.
    /// </summary>
    public int MinConcurrency { get; set; } = 1;

    /// <summary>
    /// Maximum concurrency limit per host.
    /// </summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>
    /// If EWMA latency exceeds this, concurrency will be reduced.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; } = 2000;

    /// <summary>
    /// If EWMA latency is consistently below this, concurrency may be increased.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; } = 500;

    /// <summary>
    /// Error rate threshold above which concurrency will be reduced.
    /// </summary>
    public double ErrorRateThreshold { get; set; } = 0.1;

    /// <summary>
    /// Minimum time between concurrency adjustments.
    /// </summary>
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum spacing between request start times per host.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.Zero;
}
