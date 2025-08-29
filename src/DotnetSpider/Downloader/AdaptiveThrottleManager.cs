using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DotnetSpider.Downloader;

/// <summary>
/// Manages a collection of per-host <see cref="HostGate"/>s used to implement adaptive throttling. Also periodically
/// cleans up gates for hosts that have not been used recently.
/// </summary>
public class AdaptiveThrottleManager : IDisposable
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly ConcurrentDictionary<string, HostGate> _hostGates = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public AdaptiveThrottleManager(AdaptiveThrottleOptions options)
    {
        _options = options;
        // Periodically clean up any host gates that have become inactive.
        // Here we arbitrarily pick a cleanup period of 10 minutes.
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    internal HostGate GetHostGate(string host)
    {
        return _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
    }

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
        foreach (var gate in _hostGates.Values)
        {
            list.Add(gate.GetStats());
        }
        return list.ToArray();
    }

    private void Cleanup(object state)
    {
        // remove gates that have not seen traffic for some time
        // This is a stub: HostGate does not currently record last activity time explicitly.
        // For now we will keep all gates indefinitely. Extension: track last request timestamp and purge.
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cleanupTimer.Dispose();
    }
}
