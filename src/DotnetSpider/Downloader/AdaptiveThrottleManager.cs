using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DotnetSpider.Downloader;

/// <summary>
/// Manages a collection of <see cref="HostGate"/>s keyed by host and
/// coordinates cleanup of inactive hosts over time.
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
        // periodically prune host gates if desired
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public HostGate GetHostGate(string host)
    {
        return _hostGates.GetOrAdd(host, _ => new HostGate(_options));
    }

    private void Cleanup(object state)
    {
        // There is no cleanup logic yet. Could remove gates for hosts
        // that have not seen traffic in a long time. Left as extension point.
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer?.Dispose();
            _disposed = true;
        }
    }
}
