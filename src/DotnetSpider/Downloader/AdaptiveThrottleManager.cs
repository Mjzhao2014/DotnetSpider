using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DotnetSpider.Downloader;

/// <summary>
/// A simple manager that tracks active host gates used for per-host concurrency
/// and adaptive throttling. A cleanup timer prunes gates that have not been used recently.
/// </summary>
public class AdaptiveThrottleManager : IDisposable
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly ConcurrentDictionary<string, HostGate> _hostGates;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public AdaptiveThrottleManager(AdaptiveThrottleOptions options)
    {
        _options = options;
        _hostGates = new ConcurrentDictionary<string, HostGate>(StringComparer.OrdinalIgnoreCase);
        // Periodically remove unused gates (e.g. hosts not hit for minutes)
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Obtain or create the HostGate tracking concurrency for the given host.
    /// </summary>
    internal HostGate GetOrCreateGate(string host)
    {
        return _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
    }

    /// <summary>
    /// Exposes current stats for a given host or null if no gate exists.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        if (_hostGates.TryGetValue(host, out var gate))
        {
            return gate.GetStats();
        }
        return null;
    }

    public HostGateStats[] GetAllHostStats()
    {
        var list = new System.Collections.Generic.List<HostGateStats>();
        foreach (var kv in _hostGates)
        {
            list.Add(kv.Value.GetStats());
        }
        return list.ToArray();
    }

    private void Cleanup(object state)
    {
        if (_disposed) return;
        var threshold = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var kv in _hostGates)
        {
            if (kv.Value.LastUsed < threshold)
            {
                _hostGates.TryRemove(kv.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cleanupTimer?.Dispose();
        }
    }
}
