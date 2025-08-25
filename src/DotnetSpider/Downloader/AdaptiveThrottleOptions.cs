using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Configuration options for adaptive throttling behaviors.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Flag to enable/disable adaptive throttling logic. When false, the adaptive downloader
    /// will simply delegate to the base <see cref="HttpClientDownloader"/>.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; } = false;

    /// <summary>
    /// Maximum number of retry attempts for transient failures before giving up.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Upper cap on delay applied between retry attempts.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Smoothing factor for the moving average of latency observations. 0 &lt; alpha &lt;= 1.
    /// Higher alpha gives more weight to recent samples.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.3;

    /// <summary>
    /// Lower bound on per-host concurrency.
    /// </summary>
    public int MinConcurrency { get; set; } = 1;

    /// <summary>
    /// Upper bound on per-host concurrency.
    /// </summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>
    /// Threshold of latency in milliseconds above which we consider a host to be
    /// experiencing elevated latency and should back off concurrency.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; } = 5000;

    /// <summary>
    /// Threshold of latency in milliseconds below which we consider a host to be
    /// fast and can safely increase concurrency.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; } = 200;

    /// <summary>
    /// Fraction of errors/total requests beyond which we consider a host unhealthy and back off.
    /// </summary>
    public double ErrorRateThreshold { get; set; } = 0.2;

    /// <summary>
    /// Minimum interval between successive concurrency adjustments.
    /// </summary>
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional minimum spacing between requests started for the same host.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.Zero;
}
