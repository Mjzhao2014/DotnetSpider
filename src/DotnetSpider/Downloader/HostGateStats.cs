namespace DotnetSpider.Downloader;

/// <summary>
/// Summary snapshot of the adaptive throttle state for an individual host.
/// </summary>
public class HostGateStats
{
    public string Host { get; set; }

    public double EwmaLatency { get; set; }

    /// <summary>
    /// Current concurrency limit being applied for this host.
    /// </summary>
    public int CurrentConcurrency { get; set; }

    /// <summary>
    /// Fraction of observed requests that resulted in errors.
    /// </summary>
    public double ErrorRate { get; set; }

    public long TotalRequests { get; set; }

    public long TotalErrors { get; set; }
}
