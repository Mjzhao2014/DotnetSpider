namespace DotnetSpider.Downloader;

/// <summary>
/// Snapshot of a HostGate's current adaptive throttling statistics.
/// </summary>
public class HostGateStats
{
    /// <summary>
    /// Host name this gate is tracking.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Current exponentially-weighted moving average latency in milliseconds.
    /// </summary>
    public double EwmaLatency { get; set; }

    /// <summary>
    /// Current allowed concurrency for this host.
    /// </summary>
    public int CurrentConcurrency { get; set; }

    /// <summary>
    /// Error rate computed as total errors / total requests.
    /// </summary>
    public double ErrorRate { get; set; }

    /// <summary>
    /// Total requests observed for this host.
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Total requests that resulted in transient or response errors.
    /// </summary>
    public long TotalErrors { get; set; }
}
