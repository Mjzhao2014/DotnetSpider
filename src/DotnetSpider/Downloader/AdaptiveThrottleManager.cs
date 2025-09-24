using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace DotnetSpider.Downloader;

/// <summary>
/// Maintains a collection of per-host <see cref="HostGate"/> instances used to coordinate
/// adaptive throttling across many concurrent downloads. Responsible for cleaning up
/// idle host state over time.
/// </summary>
public class AdaptiveThrottleManager : IDisposable
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly ConcurrentDictionary<string, HostGate> _hostGates;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Creates a new manager bound to the configured options.
    /// </summary>
    public AdaptiveThrottleManager(AdaptiveThrottleOptions options)
    {
        _options = options;
        _hostGates = new ConcurrentDictionary<string, HostGate>(StringComparer.OrdinalIgnoreCase);
        // Periodically remove host gates which have been idle for longer than a few minutes.
        _cleanupTimer = new Timer(CleanupExpiredHostGates, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Returns the <see cref="HostGate"/> instance for the given host, creating if necessary.
    /// </summary>
    internal HostGate GetHostGate(string host)
    {
        return _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
    }

    /// <summary>
    /// Returns a snapshot of the current state for a particular host gate, if present.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        return _hostGates.TryGetValue(host, out var gate) ? gate.GetStats() : null;
    }

    /// <summary>
    /// Returns snapshots of all host gates currently tracked.
    /// </summary>
    public HostGateStats[] GetAllHostStats()
    {
        return _hostGates.Values.Select(x => x.GetStats()).ToArray();
    }

    private void CleanupExpiredHostGates(object state)
    {
        if (_disposed) return;
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var kv in _hostGates)
        {
            if (kv.Value.LastActive < cutoff)
            {
                _hostGates.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>
    /// Disposes the cleanup timer.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _cleanupTimer?.Dispose();
    }
}
