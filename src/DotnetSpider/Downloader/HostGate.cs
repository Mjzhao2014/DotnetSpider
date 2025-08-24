using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Per-host concurrency gate and adaptive throttle state machine.
/// </summary>
internal class HostGate
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly string _host;
    private readonly object _syncRoot = new();
    private int _concurrencyLimit;
    private int _currentConcurrency;
    private readonly Queue<TaskCompletionSource<bool>> _waitQueue = new();
    private DateTime _lastRequestStamp = DateTime.MinValue;
    private DateTime _lastAdjustment = DateTime.MinValue;
    private double _ewmaLatency;
    private long _totalRequests;
    private long _totalErrors;

    public HostGate(string host, AdaptiveThrottleOptions options)
    {
        _host = host;
        _options = options;
        _concurrencyLimit = options.MinConcurrency;
    }

    /// <summary>
    /// Wait asynchronously until allowed to issue a new request to this host.
    /// This respects both the current concurrency limit and any minimum request spacing configured.
    /// </summary>
    public async Task WaitAsync()
    {
        TaskCompletionSource<bool>? waitTcs = null;
        lock (_syncRoot)
        {
            if (_currentConcurrency >= _concurrencyLimit)
            {
                waitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitQueue.Enqueue(waitTcs);
            }
            else
            {
                _currentConcurrency++;
            }
        }

        if (waitTcs != null)
        {
            await waitTcs.Task.ConfigureAwait(false);
        }

        // Enforce minimum spacing between request start times
        TimeSpan delay = TimeSpan.Zero;
        lock (_syncRoot)
        {
            var now = DateTime.UtcNow;
            var nextAllowed = _lastRequestStamp + _options.RequestSpacing;
            if (nextAllowed > now)
            {
                delay = nextAllowed - now;
            }
            _lastRequestStamp = now + delay;
        }
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Release concurrency slot and update adaptive metrics.
    /// </summary>
    public void Release(bool isError, double elapsedMilliseconds)
    {
        lock (_syncRoot)
        {
            _currentConcurrency = Math.Max(0, _currentConcurrency - 1);
            _totalRequests++;
            if (isError)
            {
                _totalErrors++;
            }
            if (elapsedMilliseconds >= 0)
            {
                if (_ewmaLatency <= 0)
                {
                    _ewmaLatency = elapsedMilliseconds;
                }
                else
                {
                    _ewmaLatency = (1 - _options.EwmaAlpha) * _ewmaLatency + _options.EwmaAlpha * elapsedMilliseconds;
                }
            }
            AdjustConcurrencyLimitIfNeeded();
            // Wake any queued waiters if we have capacity after adjusting limit
            while (_waitQueue.Count > 0 && _currentConcurrency < _concurrencyLimit)
            {
                var next = _waitQueue.Dequeue();
                _currentConcurrency++;
                next.SetResult(true);
            }
        }
    }

    private void AdjustConcurrencyLimitIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now - _lastAdjustment < _options.CooldownPeriod)
        {
            return;
        }
        var errorRate = _totalRequests > 0 ? (double)_totalErrors / _totalRequests : 0.0;
        if (errorRate > _options.ErrorRateThreshold || _ewmaLatency > _options.MaxLatencyThresholdMs)
        {
            if (_concurrencyLimit > _options.MinConcurrency)
            {
                _concurrencyLimit--;
                _lastAdjustment = now;
            }
        }
        else if (_ewmaLatency < _options.MinLatencyThresholdMs && errorRate < _options.ErrorRateThreshold)
        {
            if (_concurrencyLimit < _options.MaxConcurrency)
            {
                _concurrencyLimit++;
                _lastAdjustment = now;
            }
        }
    }

    public HostGateStats GetStats()
    {
        lock (_syncRoot)
        {
            return new HostGateStats
            {
                Host = _host,
                EwmaLatency = _ewmaLatency,
                CurrentConcurrency = _concurrencyLimit,
                ErrorRate = _totalRequests > 0 ? (double)_totalErrors / _totalRequests : 0.0,
                TotalRequests = _totalRequests,
                TotalErrors = _totalErrors
            };
        }
    }
}
