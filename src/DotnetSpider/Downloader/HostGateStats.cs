namespace DotnetSpider.Downloader;

/// <summary>
/// Snapshot of per-host throttle state, useful for diagnostics.
/// </summary>
public class HostGateStats
{
    /// <summary>
    /// Host name for this gate.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Current exponentially weighted moving average of response latency in milliseconds.
    /// </summary>
    public double EwmaLatency { get; set; }

    /// <summary>
    /// Current concurrency limit for the host.
    /// </summary>
    public int CurrentConcurrency { get; set; }

    /// <summary>
    /// Fraction of total requests that have resulted in error responses.
    /// </summary>
    public double ErrorRate { get; set; }

    /// <summary>
    /// Total requests sent to this host.
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Total errors observed for this host.
    /// </summary>
    public long TotalErrors { get; set; }
}
