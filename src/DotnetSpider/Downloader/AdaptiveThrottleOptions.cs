using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Options to control the adaptive throttling behavior of <see cref="AdaptiveHttpClientDownloader"/>.
/// When enabled, each host will maintain its own moving average latency, error counts and concurrency
/// limits and requests will be paced and retried respecting these settings.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Enable or disable the adaptive throttling feature globally.
    /// When disabled, the <see cref="AdaptiveHttpClientDownloader"/> will simply defer to the
    /// base <see cref="HttpClientDownloader"/> without per-host gating.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; } = true;

    /// <summary>
    /// How many times to retry transient failures like timeouts or 5xx/429 status codes.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum delay between retry attempts. When computing exponential backoff this cap
    /// will be used to bound the delay.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Alpha factor for the EWMA latency computation.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.3;

    /// <summary>
    /// Minimum allowed concurrency per host. Concurrency will never be reduced below this value.
    /// </summary>
    public int MinConcurrency { get; set; } = 1;

    /// <summary>
    /// Maximum allowed concurrency per host.
    /// </summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>
    /// Upper bound on latency that triggers concurrency reduction when exceeded.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; } = 5000;

    /// <summary>
    /// Lower bound on latency under which we can consider increasing concurrency
    /// once other conditions like error rate and cooldown are satisfied.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; } = 100;

    /// <summary>
    /// Error rate above which concurrency will be reduced.
    /// This is computed as total errors / total requests for the host.
    /// </summary>
    public double ErrorRateThreshold { get; set; } = 0.1;

    /// <summary>
    /// Cooldown period after reducing concurrency before we consider increasing it again.
    /// </summary>
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum spacing between successive requests to the same host.
    /// If greater than zero, requests will be delayed so that at least this interval
    /// elapses between request start times, regardless of concurrency.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.Zero;
}
