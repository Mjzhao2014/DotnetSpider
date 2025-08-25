using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Manages per-host concurrency gates and adaptive state for throttling downloads.
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
        // periodically remove host gates that have not been used for a while (10 min)
        _cleanupTimer = new Timer(RemoveStaleGates, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public HostGate GetHostGate(string host)
    {
        return _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
    }

    public HostGateStats GetHostStats(string host)
    {
        return _hostGates.TryGetValue(host, out var gate) ? gate.GetStats() : null;
    }

    public HostGateStats[] GetAllHostStats()
    {
        return _hostGates.Values.Select(g => g.GetStats()).ToArray();
    }

    private void RemoveStaleGates(object state)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var kvp in _hostGates)
        {
            var gate = kvp.Value;
            if (gate.LastUsed < cutoff)
            {
                _hostGates.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
    }

    /// <summary>
    /// Per-host adaptive concurrency controller.
    /// </summary>
    public sealed class HostGate
    {
        private readonly string _host;
        private readonly AdaptiveThrottleOptions _options;
        private readonly object _lock = new();
        private int _concurrencyLimit;
        private int _currentUsage;
        private double _ewmaLatency;
        private long _totalRequests;
        private long _totalErrors;
        private DateTime _lastAdjustment;
        private TaskCompletionSource<bool> _waiter;

        public HostGate(string host, AdaptiveThrottleOptions options)
        {
            _host = host;
            _options = options;
            _concurrencyLimit = options.MinConcurrency;
            _currentUsage = 0;
            _ewmaLatency = 0;
            _totalRequests = 0;
            _totalErrors = 0;
            _lastAdjustment = DateTime.MinValue;
            LastUsed = DateTime.MinValue;
        }

        /// <summary>
        /// Timestamp of last request started for this host.
        /// </summary>
        public DateTime LastUsed { get; private set; }

        /// <summary>
        /// Asynchronously waits for permission to start a new request to this host,
        /// honoring concurrency and pacing semantics. Returns a disposable that
        /// will release concurrency when disposed.
        /// </summary>
        public async Task<IDisposable> AcquireAsync()
        {
            while (true)
            {
                TaskCompletionSource<bool> toWait = null;
                TimeSpan delay = TimeSpan.Zero;
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    // ensure we do not exceed concurrency
                    if (_currentUsage < _concurrencyLimit)
                    {
                        // ensure minimal spacing between sequential request starts
                        var sinceLast = now - LastUsed;
                        if (sinceLast < _options.RequestSpacing)
                        {
                            delay = _options.RequestSpacing - sinceLast;
                        }
                        else
                        {
                            _currentUsage++;
                            LastUsed = now;
                            return new Releaser(this);
                        }
                    }
                    else
                    {
                        // no slots available: set up a waiter
                        if (_waiter == null)
                        {
                            _waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                        toWait = _waiter;
                    }
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                    continue;
                }

                if (toWait != null)
                {
                    await toWait.Task;
                    continue;
                }
            }
        }

        private void Release()
        {
            TaskCompletionSource<bool> toRelease = null;
            lock (_lock)
            {
                if (_currentUsage > 0)
                {
                    _currentUsage--;
                }
                if (_waiter != null && _currentUsage < _concurrencyLimit)
                {
                    toRelease = _waiter;
                    _waiter = null;
                }
            }
            toRelease?.TrySetResult(true);
        }

        /// <summary>
        /// Update EWMA latency and error counts, and adjust concurrency if needed.
        /// </summary>
        public void UpdateMetrics(int latencyMs, bool success)
        {
            lock (_lock)
            {
                _totalRequests++;
                if (!success)
                {
                    _totalErrors++;
                }
                // initialize ewma if first sample
                if (_totalRequests == 1)
                {
                    _ewmaLatency = latencyMs;
                }
                else
                {
                    _ewmaLatency = (1 - _options.EwmaAlpha) * _ewmaLatency + _options.EwmaAlpha * latencyMs;
                }

                var now = DateTime.UtcNow;
                if (now - _lastAdjustment < _options.CooldownPeriod)
                {
                    return;
                }

                var errorRate = _totalRequests == 0 ? 0 : (double)_totalErrors / _totalRequests;

                bool increased = false;
                if (errorRate > _options.ErrorRateThreshold || _ewmaLatency > _options.MaxLatencyThresholdMs)
                {
                    if (_concurrencyLimit > _options.MinConcurrency)
                    {
                        _concurrencyLimit--;
                    }
                }
                else if (errorRate <= _options.ErrorRateThreshold && _ewmaLatency < _options.MinLatencyThresholdMs)
                {
                    if (_concurrencyLimit < _options.MaxConcurrency)
                    {
                        _concurrencyLimit++;
                        increased = true;
                    }
                }

                if (increased || errorRate > _options.ErrorRateThreshold || _ewmaLatency > _options.MaxLatencyThresholdMs)
                {
                    _lastAdjustment = now;
                }
            }
        }

        public HostGateStats GetStats()
        {
            lock (_lock)
            {
                var stats = new HostGateStats
                {
                    Host = _host,
                    EwmaLatency = _ewmaLatency,
                    CurrentConcurrency = _concurrencyLimit,
                    ErrorRate = _totalRequests == 0 ? 0 : (double)_totalErrors / _totalRequests,
                    TotalRequests = _totalRequests,
                    TotalErrors = _totalErrors
                };
                return stats;
            }
        }

        private sealed class Releaser : IDisposable
        {
            private HostGate _gate;

            public Releaser(HostGate gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                if (_gate != null)
                {
                    _gate.Release();
                    _gate = null;
                }
            }
        }
    }
}
