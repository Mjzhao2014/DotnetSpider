using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Options to configure adaptive throttling behaviour of AdaptiveHttpClientDownloader.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Whether adaptive throttling is enabled.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; } = false;

    /// <summary>
    /// If transient failures should be retried, the maximum attempts to retry.
    /// Not currently used by AdaptiveThrottleManager, but surfaced here for future enhancements.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 0;

    /// <summary>
    /// If transient failures should be retried, the maximum delay between retries.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Alpha used to smooth observed latency into an exponentially-weighted moving average.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.3;

    /// <summary>
    /// The minimum concurrency allowed per host.
    /// </summary>
    public int MinConcurrency { get; set; } = 1;

    /// <summary>
    /// The maximum concurrency allowed per host.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Threshold in milliseconds above which latency is considered high and concurrency will be reduced.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; } = 30000;

    /// <summary>
    /// Threshold in milliseconds below which latency is considered low and concurrency may be increased.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; } = 100;

    /// <summary>
    /// Acceptable error rate for a host before concurrency is reduced.
    /// </summary>
    public double ErrorRateThreshold { get; set; } = 0.5;

    /// <summary>
    /// Minimum time between concurrency adjustments to avoid oscillation.
    /// </summary>
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum spacing between starting requests to a single host.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.Zero;
}
