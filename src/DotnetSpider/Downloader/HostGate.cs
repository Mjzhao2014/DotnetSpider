using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Maintains per-host adaptive throttling state and concurrency gating.
/// A HostGate limits how many requests may be sent concurrently to a given host and
/// applies optional spacing between requests. It captures latency/error observations
/// to adjust its concurrency limits over time based on AdaptiveThrottleOptions.
/// </summary>
internal class HostGate
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource<bool>> _waitQueue = new();
    private int _concurrencyInFlight;
    private int _concurrencyLimit;
    private DateTimeOffset _lastRequestStart;

    private double _ewmaLatency;
    private long _totalRequests;
    private long _totalErrors;

    public HostGate(string host, AdaptiveThrottleOptions options)
    {
        Host = host;
        _options = options;
        _concurrencyLimit = Math.Max(1, options.MinConcurrency);
    }

    /// <summary>
    /// Host name this gate corresponds to.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Timestamp of the last request start for this host gate.
    /// Used to enforce RequestSpacing.
    /// </summary>
    public DateTimeOffset LastActive { get; private set; }

    /// <summary>
    /// Acquire a concurrency slot for this host. If the current number of in-flight requests
    /// meets or exceeds the concurrency limit, the returned task will not complete until a slot
    /// becomes available. When a slot becomes available, the caller may proceed, subject to
    /// optional request spacing enforcement.
    /// </summary>
    public async Task AcquireAsync()
    {
        TaskCompletionSource<bool> waiter = null;
        lock (_lock)
        {
            if (_concurrencyInFlight < _concurrencyLimit)
            {
                _concurrencyInFlight++;
            }
            else
            {
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitQueue.Enqueue(waiter);
            }
        }
        if (waiter != null)
        {
            await waiter.Task.ConfigureAwait(false);
        }

        // optional spacing between requests
        if (_options.RequestSpacing > TimeSpan.Zero)
        {
            var now = DateTimeOffset.UtcNow;
            var sinceLast = now - _lastRequestStart;
            if (sinceLast < _options.RequestSpacing)
            {
                var delay = _options.RequestSpacing - sinceLast;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }
        }
        _lastRequestStart = DateTimeOffset.UtcNow;
        LastActive = _lastRequestStart;
    }

    /// <summary>
    /// Release a previously acquired concurrency slot, updating EWMA latency and error counts.
    /// Adjusts concurrency limits upward/downward based on thresholds.
    /// </summary>
    public void Release(bool error, long latencyMilliseconds)
    {
        TaskCompletionSource<bool> nextToRelease = null;
        lock (_lock)
        {
            if (_concurrencyInFlight > 0)
            {
                _concurrencyInFlight--;
            }

            // update stats
            _totalRequests++;
            if (error)
            {
                _totalErrors++;
            }

            if (latencyMilliseconds >= 0)
            {
                if (_ewmaLatency == 0)
                {
                    _ewmaLatency = latencyMilliseconds;
                }
                else
                {
                    _ewmaLatency = (1 - _options.EwmaAlpha) * _ewmaLatency + _options.EwmaAlpha * latencyMilliseconds;
                }
            }

            AdjustConcurrencyNoLock();

            // if we have waiters and capacity, signal one
            if (_waitQueue.Count > 0 && _concurrencyInFlight < _concurrencyLimit)
            {
                nextToRelease = _waitQueue.Dequeue();
                _concurrencyInFlight++;
            }
        }

        nextToRelease?.SetResult(true);
    }

    /// <summary>
    /// Compute and update concurrency limit based on latency/error thresholds.
    /// </summary>
    private void AdjustConcurrencyNoLock()
    {
        var errorRate = _totalRequests > 0 ? (double)_totalErrors / _totalRequests : 0.0;
        var oldLimit = _concurrencyLimit;
        var newLimit = oldLimit;
        if (errorRate > _options.ErrorRateThreshold || (_ewmaLatency > 0 && _ewmaLatency > _options.MaxLatencyThresholdMs))
        {
            newLimit = Math.Max(_options.MinConcurrency, _concurrencyLimit - 1);
        }
        else if (_ewmaLatency > 0 && _ewmaLatency < _options.MinLatencyThresholdMs && errorRate < _options.ErrorRateThreshold)
        {
            newLimit = Math.Min(_options.MaxConcurrency, _concurrencyLimit + 1);
        }
        if (newLimit != oldLimit)
        {
            _concurrencyLimit = newLimit;
        }
    }

    /// <summary>
    /// Produce a snapshot of current stats for external consumers.
    /// </summary>
    public HostGateStats GetStats()
    {
        var errorRate = _totalRequests > 0 ? (double)_totalErrors / _totalRequests : 0.0;
        return new HostGateStats
        {
            Host = Host,
            CurrentConcurrency = _concurrencyLimit,
            EwmaLatency = _ewmaLatency,
            TotalRequests = _totalRequests,
            TotalErrors = _totalErrors,
            ErrorRate = errorRate
        };
    }
}
