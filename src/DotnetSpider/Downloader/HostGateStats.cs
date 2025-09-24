namespace DotnetSpider.Downloader;

/// <summary>
/// Snapshot of the current adaptive throttling state for a single host.
/// </summary>
public class HostGateStats
{
    /// <summary>
    /// Host name this gate represents.
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    /// Current exponentially weighted moving average of observed latency for this host.
    /// </summary>
    public double EwmaLatency { get; set; }

    /// <summary>
    /// Current concurrency limit for this host.
    /// </summary>
    public int CurrentConcurrency { get; set; }

    /// <summary>
    /// Fraction of total requests that have resulted in an error.
    /// </summary>
    public double ErrorRate { get; set; }

    /// <summary>
    /// Total number of requests made to this host.
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Total number of error responses or failures observed.
    /// </summary>
    public long TotalErrors { get; set; }
}
