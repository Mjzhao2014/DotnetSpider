using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Options governing the adaptive throttling behavior of <see cref="AdaptiveHttpClientDownloader"/>
/// and the per-host <see cref="HostGate"/>s managed by <see cref="AdaptiveThrottleManager"/>.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Enables or disables the adaptive throttling feature. When disabled the adaptive downloader
    /// will fall back on fixed behavior.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; } = true;

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum delay between retry attempts. If a backoff or Retry-After delay exceeds this,
    /// it will be capped to this value.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Alpha parameter for the exponentially weighted moving average of observed latencies.
    /// New samples are incorporated as L = (1-α)L + α⋅sample.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.3;

    /// <summary>
    /// Minimum concurrency allowed per host gate.
    /// </summary>
    public int MinConcurrency { get; set; } = 1;

    /// <summary>
    /// Maximum concurrency allowed per host gate.
    /// </summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>
    /// Upper latency threshold in milliseconds for considering a host slow. If EWMA latency exceeds
    /// this threshold the concurrency will be aggressively reduced.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; } = 2000;

    /// <summary>
    /// Lower latency threshold in milliseconds for considering a host fast. If EWMA latency is
    /// persistently below this threshold concurrency may be increased.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; } = 100;

    /// <summary>
    /// Threshold on error rate above which concurrency will be reduced.
    /// Error rate is computed as total errors / total requests.
    /// </summary>
    public double ErrorRateThreshold { get; set; } = 0.5;

    /// <summary>
    /// Cooldown period between concurrency adjustments. Prevents thrashing.
    /// </summary>
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional minimum spacing to enforce between successive requests to the same host.
    /// When set to non-zero, ensures a grace period between starting new requests.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.Zero;
}
