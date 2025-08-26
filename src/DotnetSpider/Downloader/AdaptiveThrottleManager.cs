using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Maintains a registry of per-host <see cref="HostGate"/> instances used to coordinate per-host concurrency,
/// pacing, and adaptive throttling decisions.
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
        // Clean up idle host gates periodically to prevent unbounded growth.
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Retrieve the gate for a specific host, creating it if necessary. Internal because
    /// consumer uses the gate only for coordination, not for exposure outside this assembly.
    /// </summary>
    internal HostGate GetHostGate(string host)
    {
        return _hostGates.GetOrAdd(host, h => new HostGate(h, _options));
    }

    /// <summary>
    /// Return stats for a specific host gate if present.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        if (_hostGates.TryGetValue(host, out var gate))
        {
            return gate.GetStats();
        }
        return null;
    }

    /// <summary>
    /// Return stats for all hosts currently tracked.
    /// </summary>
    public HostGateStats[] GetAllHostStats()
    {
        return _hostGates.Values.Select(g => g.GetStats()).ToArray();
    }

    private void Cleanup(object state)
    {
        // Simple expiration: remove gates that have not seen any requests in a long time.
        // Currently no explicit expiration timestamp is tracked, but could be added.
        // This stub exists to satisfy the contract; a more sophisticated cleanup could be implemented.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
    }

    /// <summary>
    /// Internal class representing per-host concurrency and EWMA latency tracking.
    /// </summary>
    internal class HostGate
    {
        private readonly AdaptiveThrottleOptions _options;
        private readonly Queue<TaskCompletionSource<object>> _waitQueue = new();
        private readonly object _lock = new();

        public string Host { get; }
        private int _concurrencyLimit;
        private int _inFlight;
        private DateTime _lastRequestStart;
        private DateTime _lastAdjustment;
        private double _ewmaLatency;
        private long _totalRequests;
        private long _totalErrors;

        public HostGate(string host, AdaptiveThrottleOptions options)
        {
            Host = host;
            _options = options;
            _concurrencyLimit = Math.Max(1, options.MinConcurrency);
            _lastAdjustment = DateTime.UtcNow;
        }

        /// <summary>
        /// Await until concurrency slot is available (respecting current concurrency limit), then enforce
        /// optional minimum spacing between starts of requests.
        /// Returns a disposable token that must be disposed on request completion to release the slot.
        /// </summary>
        public async Task<IDisposable> WaitAsync()
        {
            Task waitTask = null;
            lock (_lock)
            {
                if (_inFlight < _concurrencyLimit)
                {
                    _inFlight++;
                }
                else
                {
                    var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waitQueue.Enqueue(tcs);
                    waitTask = tcs.Task;
                }
            }
            if (waitTask != null)
            {
                await waitTask.ConfigureAwait(false);
            }
            // Enforce request spacing if configured.
            if (_options.RequestSpacing > TimeSpan.Zero)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastRequestStart;
                if (elapsed < _options.RequestSpacing)
                {
                    var delay = _options.RequestSpacing - elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                }
                _lastRequestStart = DateTime.UtcNow;
            }
            return new Release(this);
        }

        public void RecordResult(TimeSpan latency, bool isError)
        {
            lock (_lock)
            {
                _totalRequests++;
                if (isError)
                {
                    _totalErrors++;
                }
                var sample = latency.TotalMilliseconds;
                if (_totalRequests == 1)
                {
                    _ewmaLatency = sample;
                }
                else
                {
                    _ewmaLatency = (1.0 - _options.EwmaAlpha) * _ewmaLatency + _options.EwmaAlpha * sample;
                }
            }
            AdjustConcurrency();
        }

        public HostGateStats GetStats()
        {
            lock (_lock)
            {
                var errorRate = _totalRequests == 0 ? 0.0 : (double)_totalErrors / _totalRequests;
                return new HostGateStats
                {
                    Host = Host,
                    EwmaLatency = _ewmaLatency,
                    CurrentConcurrency = _concurrencyLimit,
                    TotalRequests = _totalRequests,
                    TotalErrors = _totalErrors,
                    ErrorRate = errorRate
                };
            }
        }

        private void AdjustConcurrency()
        {
            if (_options.MaxConcurrency <= _options.MinConcurrency) return;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (now - _lastAdjustment < _options.CooldownPeriod) return;
                var errorRate = _totalRequests == 0 ? 0.0 : (double)_totalErrors / _totalRequests;
                bool decreased = false;
                if ((_ewmaLatency > _options.MaxLatencyThresholdMs) || (errorRate >= _options.ErrorRateThreshold))
                {
                    if (_concurrencyLimit > _options.MinConcurrency)
                    {
                        _concurrencyLimit--;
                        decreased = true;
                    }
                }
                else if ((_ewmaLatency < _options.MinLatencyThresholdMs) && (errorRate <= _options.ErrorRateThreshold))
                {
                    if (_concurrencyLimit < _options.MaxConcurrency)
                    {
                        _concurrencyLimit++;
                    }
                }
                if (decreased || _concurrencyLimit > _inFlight)
                {
                    // If concurrency increased, release waiting tasks up to new limit.
                    while (_waitQueue.Count > 0 && _inFlight < _concurrencyLimit)
                    {
                        var next = _waitQueue.Dequeue();
                        _inFlight++;
                        next.SetResult(null);
                    }
                }
                _lastAdjustment = now;
            }
        }

        private class Release : IDisposable
        {
            private readonly HostGate _hostGate;
            private bool _disposed;
            public Release(HostGate hostGate)
            {
                _hostGate = hostGate;
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                TaskCompletionSource<object> unblock = null;
                lock (_hostGate._lock)
                {
                    _hostGate._inFlight--;
                    if (_hostGate._waitQueue.Count > 0 && _hostGate._inFlight < _hostGate._concurrencyLimit)
                    {
                        unblock = _hostGate._waitQueue.Dequeue();
                        _hostGate._inFlight++;
                    }
                }
                unblock?.SetResult(null);
            }
        }
    }
}
