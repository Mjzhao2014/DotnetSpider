namespace DotnetSpider.Downloader;

/// <summary>
/// Snapshot of internal statistics for a host gate.
/// Useful for introspection of per-host throttling state.
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
