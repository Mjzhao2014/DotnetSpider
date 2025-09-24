using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Options controlling adaptive per-host throttling.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Whether adaptive throttling is enabled. If false, downloader behaves like the standard HttpClientDownloader.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; }

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    public int MaxRetryAttempts { get; set; }

    /// <summary>
    /// Maximum delay to wait between retries. Exponential backoff will be capped at this value.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Alpha smoothing factor applied when updating the EWMA latency.
    /// </summary>
    public double EwmaAlpha { get; set; }

    /// <summary>
    /// Minimum concurrency to maintain per host.
    /// </summary>
    public int MinConcurrency { get; set; }

    /// <summary>
    /// Maximum concurrency to allow per host.
    /// </summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// If the EWMA latency in milliseconds exceeds this threshold, concurrency will be reduced.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; }

    /// <summary>
    /// If the EWMA latency in milliseconds is below this threshold and error rate is low, concurrency may be increased.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; }

    /// <summary>
    /// Error rate threshold beyond which concurrency will be reduced.
    /// </summary>
    public double ErrorRateThreshold { get; set; }

    /// <summary>
    /// Minimum delay to impose between the start of successive requests to a single host.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; }
}
