using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Throttling;

public class AdaptiveThrottleManager : IDisposable
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly ConcurrentDictionary<string, HostGate> _hostGates;
    private readonly Timer _cleanupTimer;
    private bool _disposed = false;

    public AdaptiveThrottleManager(AdaptiveThrottleOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hostGates = new ConcurrentDictionary<string, HostGate>();
        
        _cleanupTimer = new Timer(CleanupInactiveHosts, null, 
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public HostGate GetHostGate(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host cannot be null or empty", nameof(host));

        return _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
    }

    public async Task<IDisposable> AcquireAsync(string host, CancellationToken cancellationToken = default)
    {
        var hostGate = GetHostGate(host);
        return await hostGate.AcquireAsync(cancellationToken);
    }

    public void RecordLatency(string host, double latencyMs)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        
        var hostGate = GetHostGate(host);
        hostGate.RecordLatency(latencyMs);
    }

    public void RecordError(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        
        var hostGate = GetHostGate(host);
        hostGate.RecordError();
    }

    public int GetHostCount()
    {
        return _hostGates.Count;
    }

    public HostGateStats GetHostStats(string host)
    {
        if (!_hostGates.TryGetValue(host, out var hostGate))
            return null;

        return new HostGateStats
        {
            Host = host,
            EwmaLatency = hostGate.EwmaLatency,
            CurrentConcurrency = hostGate.CurrentConcurrency,
            ErrorRate = hostGate.ErrorRate,
            TotalRequests = hostGate.TotalRequests,
            TotalErrors = hostGate.TotalErrors
        };
    }

    public HostGateStats[] GetAllHostStats()
    {
        var stats = new HostGateStats[_hostGates.Count];
        var index = 0;
        
        foreach (var kvp in _hostGates)
        {
            stats[index++] = new HostGateStats
            {
                Host = kvp.Key,
                EwmaLatency = kvp.Value.EwmaLatency,
                CurrentConcurrency = kvp.Value.CurrentConcurrency,
                ErrorRate = kvp.Value.ErrorRate,
                TotalRequests = kvp.Value.TotalRequests,
                TotalErrors = kvp.Value.TotalErrors
            };
        }
        
        return stats;
    }

    private void CleanupInactiveHosts(object state)
    {
        if (_disposed) return;

        var cutoffTime = DateTime.UtcNow.Subtract(TimeSpan.FromHours(1));
        var toRemove = new List<string>();

        foreach (var kvp in _hostGates)
        {
            if (kvp.Value.TotalRequests == 0)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var host in toRemove)
        {
            _hostGates.TryRemove(host, out _);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer?.Dispose();
            _hostGates.Clear();
            _disposed = true;
        }
    }
}

public class HostGateStats
{
    public string Host { get; set; }
    public double EwmaLatency { get; set; }
    public int CurrentConcurrency { get; set; }
    public double ErrorRate { get; set; }
    public long TotalRequests { get; set; }
    public long TotalErrors { get; set; }
}