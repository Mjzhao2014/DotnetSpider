namespace DotnetSpider.Downloader;

/// <summary>
/// Snapshot of a host's current gating and performance state.
/// This can be used for diagnostics and monitoring of the adaptive
/// throttle manager.
/// </summary>
public class HostGateStats
{
    public string Host { get; set; }
    public double EwmaLatency { get; set; }
    public int CurrentConcurrency { get; set; }
    public double ErrorRate { get; set; }
    public long TotalRequests { get; set; }
    public long TotalErrors { get; set; }
}
