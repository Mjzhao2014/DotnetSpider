using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace DotnetSpider.Downloader;

/// <summary>
/// Manages per-host <see cref="HostGate"/> instances and exposes snapshots
/// of host stats for diagnostics.
/// Tracks hosts in a concurrent dictionary and periodically cleans
/// out unused host gates.
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
        _hostGates = new ConcurrentDictionary<string, HostGate>();
        // Periodic cleanup of unused hosts
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public HostGateStats GetHostStats(string host)
    {
        if (_hostGates.TryGetValue(host, out var gate))
        {
            return gate.ToStats();
        }
        return null;
    }

    public HostGateStats[] GetAllHostStats()
    {
        return _hostGates.Values.Select(g => g.ToStats()).ToArray();
    }

    /// <summary>
    /// Get or create the host gate for the given host.
    /// </summary>
    internal HostGate GetHostGate(string host)
    {
        return _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
    }

    private void Cleanup(object state)
    {
        var threshold = DateTime.UtcNow - TimeSpan.FromMinutes(30);
        foreach (var kvp in _hostGates)
        {
            if (kvp.Value.LastUsed < threshold)
            {
                _hostGates.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer?.Dispose();
    }
}
