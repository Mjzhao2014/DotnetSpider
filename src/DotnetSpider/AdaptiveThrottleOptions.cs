namespace DotnetSpider;

/// <summary>
/// Configuration for adaptive request throttling on a per-host basis.
/// </summary>
public class AdaptiveThrottleOptions
{
    /// <summary>
    /// Minimum number of in-flight requests to allow per-host.
    /// </summary>
    public int MinConcurrency { get; set; } = 1;

    /// <summary>
    /// Maximum number of in-flight requests to allow per-host.
    /// </summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>
    /// Smoothing factor for EWMA latency and error calculations.
    /// 0 &lt; alpha ≤ 1.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.2;

    /// <summary>
    /// When the exponentially smoothed error probability exceeds this threshold,
    /// concurrency will be scaled down. 0.0 - 1.0.
    /// </summary>
    public double ErrorRateThreshold { get; set; } = 0.5;

    /// <summary>
    /// If average latency rises above this threshold (ms), concurrency will be
    /// reduced. If below <see cref="LatencyLowThreshold"/>, concurrency may be increased.
    /// </summary>
    public int LatencyHighThreshold { get; set; } = 2000;

    /// <summary>
    /// If average latency stays below this threshold (ms), concurrency may be scaled up.
    /// </summary>
    public int LatencyLowThreshold { get; set; } = 200;

    /// <summary>
    /// Cooldown interval (ms) between concurrency adjustments to avoid oscillation.
    /// </summary>
    public int CooldownMilliseconds { get; set; } = 1000;
}
