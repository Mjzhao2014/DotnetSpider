namespace DotnetSpider.Downloader;

/// <summary>
/// Snapshot of current per-host throttling state for diagnostic purposes.
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
