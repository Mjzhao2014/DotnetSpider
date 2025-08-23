using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Maintains the per-host state used to gate concurrency and pacing according to live performance signals.
/// Hosts are lazily created when first encountered and cleaned up after periods of inactivity.
/// </summary>
public class AdaptiveThrottleManager : IDisposable
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly ConcurrentDictionary<string, HostGate> _hostGates;
    private readonly Timer _cleanupTimer;
    private bool _disposed;
    private static readonly IDisposable _noopDisposable = new NoopDisposable();

    public AdaptiveThrottleManager(AdaptiveThrottleOptions options)
    {
        _options = options;
        _hostGates = new ConcurrentDictionary<string, HostGate>(StringComparer.OrdinalIgnoreCase);
        // Periodically clean up unused host gates, e.g. every 10 minutes
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// Acquire a concurrency slot and apply pacing for the given host. Returns a permit which must be disposed to release the slot.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string host, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableAdaptiveThrottling || string.IsNullOrEmpty(host))
        {
            return _noopDisposable;
        }
        var gate = _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
        return await gate.AcquireAsync(cancellationToken);
    }

    /// <summary>
    /// Update latency/error metrics for the given host after a request completes.
    /// </summary>
    public void Record(string host, int latencyMs, bool isError)
    {
        if (!_options.EnableAdaptiveThrottling || string.IsNullOrEmpty(host))
        {
            return;
        }
        if (_hostGates.TryGetValue(host, out var gate))
        {
            gate.UpdateMetrics(latencyMs, isError);
        }
    }

    /// <summary>
    /// Get current stats snapshot for the given host.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }
        if (_hostGates.TryGetValue(host, out var gate))
        {
            return gate.GetStats();
        }
        return null;
    }

    private void Cleanup(object state)
    {
        var cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(30));
        foreach (var kvp in _hostGates)
        {
            if (kvp.Value.LastTouched < cutoff)
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
        foreach (var gate in _hostGates.Values)
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// Internal per-host gate implementation embodying concurrency/spacing and adaptive logic.
    /// </summary>
    private class HostGate : IDisposable
    {
        private readonly AdaptiveThrottleOptions _options;
        private readonly SemaphoreSlim _semaphore;
        private readonly object _lock = new object();
        private int _allowedConcurrency;
        private int _issuedPermits;
        private DateTime _lastConcurrencyChange;

        public string Host { get; }
        public double EwmaLatency { get; private set; }
        public long TotalRequests { get; private set; }
        public long TotalErrors { get; private set; }
        public DateTime LastTouched { get; private set; }
        private DateTime _lastRequestStart;

        public HostGate(string host, AdaptiveThrottleOptions options)
        {
            Host = host;
            _options = options;
            _allowedConcurrency = Math.Max(1, options.MinConcurrency);
            _semaphore = new SemaphoreSlim(0, int.MaxValue);
            _issuedPermits = _allowedConcurrency;
            _semaphore.Release(_allowedConcurrency);
            _lastConcurrencyChange = DateTime.UtcNow;
            _lastRequestStart = DateTime.MinValue;
        }

        public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            LastTouched = DateTime.UtcNow;
            // Wait for a concurrency slot
            await _semaphore.WaitAsync(cancellationToken);
            // Apply request spacing
            var delay = TimeSpan.Zero;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var earliest = _lastRequestStart + _options.RequestSpacing;
                if (earliest > now)
                {
                    delay = earliest - now;
                    _lastRequestStart = earliest;
                }
                else
                {
                    _lastRequestStart = now;
                }
            }
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            return new Release(this);
        }

        public void UpdateMetrics(int latencyMs, bool isError)
        {
            lock (_lock)
            {
                TotalRequests++;
                if (isError)
                {
                    TotalErrors++;
                }
                // EWMA initialization
                if (TotalRequests == 1)
                {
                    EwmaLatency = latencyMs;
                }
                else
                {
                    EwmaLatency = (1 - _options.EwmaAlpha) * EwmaLatency + _options.EwmaAlpha * latencyMs;
                }
            }
            AdjustConcurrency();
        }

        private void AdjustConcurrency()
        {
            // Check cooldown
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (now - _lastConcurrencyChange < _options.CooldownPeriod)
                {
                    return;
                }
                var errorRate = TotalRequests == 0 ? 0 : (double)TotalErrors / TotalRequests;
                if (EwmaLatency < _options.MinLatencyThresholdMs && errorRate <= _options.ErrorRateThreshold && _allowedConcurrency < _options.MaxConcurrency)
                {
                    _allowedConcurrency++;
                    _issuedPermits++;
                    _semaphore.Release();
                    _lastConcurrencyChange = now;
                }
                else if ((EwmaLatency > _options.MaxLatencyThresholdMs || errorRate > _options.ErrorRateThreshold) && _allowedConcurrency > _options.MinConcurrency)
                {
                    _allowedConcurrency--;
                    _lastConcurrencyChange = now;
                    if (_issuedPermits > _allowedConcurrency)
                    {
                        // remove an outstanding permit if available
                        if (_semaphore.Wait(0))
                        {
                            _issuedPermits--;
                        }
                        else
                        {
                            // All permits are currently checked out; adjust accounting on next release
                            _issuedPermits--;
                        }
                    }
                }
            }
        }

        public HostGateStats GetStats()
        {
            lock (_lock)
            {
                var currentConcurrency = _allowedConcurrency;
                var errorRate = TotalRequests == 0 ? 0 : (double)TotalErrors / TotalRequests;
                return new HostGateStats
                {
                    Host = Host,
                    EwmaLatency = EwmaLatency,
                    CurrentConcurrency = currentConcurrency,
                    ErrorRate = errorRate,
                    TotalRequests = TotalRequests,
                    TotalErrors = TotalErrors
                };
            }
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }

        private class Release : IDisposable
        {
            private readonly HostGate _parent;
            private int _released;
            public Release(HostGate parent)
            {
                _parent = parent;
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    _parent._semaphore.Release();
                }
            }
        }
    }

    private class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
            // no-op
        }
    }
}
