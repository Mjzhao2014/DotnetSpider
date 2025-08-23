using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Options to tune adaptive throttling behavior on a per-host basis.
/// These values control concurrency limits, EWMA smoothing, thresholds
/// for when to scale up/down concurrency, spacing between requests,
/// error classification and retry backoff caps.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Enable or disable adaptive throttling. When disabled the downloader
    /// behaves like a regular HttpClientDownloader.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; } = true;

    /// <summary>
    /// Maximum number of retry attempts for transient failures (timeouts, 429, 5xx).
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum delay to cap exponential retry back-off. Defaults to 3 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Alpha parameter for EWMA latency smoothing. Typically small (0-1).
    /// Closer to 1 reacts faster to changes; closer to 0 smooths more.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.3;

    /// <summary>
    /// Minimum concurrency per host.
    /// </summary>
    public int MinConcurrency { get; set; } = 1;

    /// <summary>
    /// Maximum concurrency per host.
    /// </summary>
    public int MaxConcurrency { get; set; } = 6;

    /// <summary>
    /// If EWMA latency climbs above this threshold (ms) we consider the host slow and scale down.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; } = 2000;

    /// <summary>
    /// If EWMA latency consistently below this threshold (ms) and errors low we scale up.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; } = 200;

    /// <summary>
    /// Error rate above this fraction will scale down concurrency.
    /// </summary>
    public double ErrorRateThreshold { get; set; } = 0.1;

    /// <summary>
    /// Minimum time between successive concurrency adjustments.
    /// </summary>
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Minimum spacing between requests to the same host.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.Zero;
}
