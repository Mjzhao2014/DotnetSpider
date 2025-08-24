using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace DotnetSpider.Downloader;

/// <summary>
/// Manages a collection of HostGates keyed by hostname. Responsible for cleaning up unused gates.
/// </summary>
public class AdaptiveThrottleManager : IDisposable
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly ConcurrentDictionary<string, HostGate> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public AdaptiveThrottleManager(AdaptiveThrottleOptions options)
    {
        _options = options;
        // Periodic cleanup of old host gates to prevent unbounded growth
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    private void Cleanup(object? _)
    {
        if (_disposed)
        {
            return;
        }
        // For now, no aggressive cleanup logic; gates will remain for lifetime.
        // Future improvements might remove gates that haven't been used for some period.
    }

    /// <summary>
    /// Get or create a HostGate for the specified host.
    /// Internal because HostGate itself is internal.
    /// </summary>
    internal HostGate GetHostGate(string host)
    {
        return _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
    }

    /// <summary>
    /// Returns diagnostic stats for a host, if we have seen it.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        if (_hostGates.TryGetValue(host, out var gate))
        {
            return gate.GetStats();
        }
        return new HostGateStats { Host = host };
    }

    /// <summary>
    /// Returns diagnostic stats for all hosts being tracked.
    /// </summary>
    public HostGateStats[] GetAllHostStats()
    {
        return _hostGates.Values.Select(g => g.GetStats()).ToArray();
    }

    public void Dispose()
    {
        _disposed = true;
        _cleanupTimer.Dispose();
    }
}
