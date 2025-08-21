using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Throttling;

public interface IHostThrottler : IDisposable
{
    Task<IDisposable> AcquireAsync(string host, CancellationToken cancellationToken = default);
    
    void RecordSuccess(string host, TimeSpan latency);
    
    void RecordError(string host, TimeSpan latency);
    
    HostMetrics GetMetrics(string host);
}

public class HostMetrics
{
    public int CurrentConcurrency { get; init; }
    public double AverageLatency { get; init; }
    public double ErrorRate { get; init; }
    public TimeSpan TimeSinceLastRequest { get; init; }
}