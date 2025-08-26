using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Options controlling the adaptive throttling and retry behavior of the <see cref="AdaptiveHttpClientDownloader"/>
/// and its per-host <see cref="AdaptiveThrottleManager"/>.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// When true, per-host adaptive throttling and retry logic will be enabled. When false,
    /// the <see cref="AdaptiveHttpClientDownloader"/> will behave like a standard <see cref="HttpClientDownloader"/>.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; }

    /// <summary>
    /// Maximum number of retry attempts for transient failures (timeouts, 429, or 5xx responses).
    /// A value of zero disables retry logic.
    /// </summary>
    public int MaxRetryAttempts { get; set; }

    /// <summary>
    /// Maximum delay between retry attempts. Exponential backoff with jitter will be capped at this value.
    /// Defaults to 3 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Alpha to use when updating EWMA latency. New samples are weighted by EwmaAlpha, existing by (1 - EwmaAlpha).
    /// </summary>
    public double EwmaAlpha { get; set; }

    /// <summary>
    /// Minimum concurrency allowed per host.
    /// </summary>
    public int MinConcurrency { get; set; }

    /// <summary>
    /// Maximum concurrency allowed per host.
    /// </summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// Upper latency threshold (in milliseconds) above which concurrency will be reduced.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; }

    /// <summary>
    /// Lower latency threshold (in milliseconds) below which concurrency may be increased.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; }

    /// <summary>
    /// Error rate threshold above which concurrency will be reduced.
    /// </summary>
    public double ErrorRateThreshold { get; set; }

    /// <summary>
    /// Minimum amount of time to wait between concurrency adjustments to prevent thrashing.
    /// </summary>
    public TimeSpan CooldownPeriod { get; set; }

    /// <summary>
    /// If greater than zero, enforces a minimum spacing between requests started for a given host.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; }
}
