using System;

namespace DotnetSpider.Downloader;

/// <summary>
/// Tuning options for adaptive per-host throttling used by <see cref="AdaptiveHttpClientDownloader"/>.
/// The defaults favor conservative crawl behavior but can be tweaked to
/// allow more aggressive or more polite crawling per host.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Toggle adaptive throttling entirely. When disabled, <see cref="AdaptiveHttpClientDownloader"/>
    /// defers to the fixed concurrency of the underlying <see cref="HttpClientDownloader"/>.
    /// </summary>
    public bool EnableAdaptiveThrottling { get; set; } = true;

    /// <summary>
    /// When true, emit conditional requests based on prior 304 caches.
    /// </summary>
    public bool EnableConditionalRequests { get; set; } = false;

    /// <summary>
    /// When true, cache full response content according to cache policy.
    /// </summary>
    public bool CacheResponseContent { get; set; } = false;

    /// <summary>
    /// Maximum retry attempts before giving up.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Upper bound on backoff delay between retries.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Alpha weighting factor for latency EWMA. Higher values weight recent observations more.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.3;

    /// <summary>
    /// Lower bound on concurrency per host.
    /// </summary>
    public int MinConcurrency { get; set; } = 1;

    /// <summary>
    /// Upper bound on concurrency per host.
    /// </summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>
    /// Consider latencies above this threshold (milliseconds) as degraded.
    /// </summary>
    public int MaxLatencyThresholdMs { get; set; } = 5000;

    /// <summary>
    /// Consider latencies below this threshold (milliseconds) as low.
    /// </summary>
    public int MinLatencyThresholdMs { get; set; } = 100;

    /// <summary>
    /// If the proportion of errors observed since last adjustment exceeds this
    /// fraction, the concurrency limit will be decreased.
    /// </summary>
    public double ErrorRateThreshold { get; set; } = 0.1;

    /// <summary>
    /// Minimum time between successive concurrency adjustments.
    /// </summary>
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum time to space successive requests for a host when concurrency is 1.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// If specified &gt; 0, cap total bytes per second downloaded per host.
    /// </summary>
    public int MaxBandwidthBytesPerSecond { get; set; } = 0;

    /// <summary>
    /// Cache entry lifetime if caching responses.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum number of cached responses kept.
    /// </summary>
    public int MaxCacheEntries { get; set; } = 1000;
}
