using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Represents a single host's adaptive throttling state including
/// moving average latency, error counts, concurrency limits,
/// and last request time used to pace requests.
/// </summary>
internal class HostGate
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly string _host;
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private int _running;
    private int _concurrencyLimit;
    private double _ewmaLatency;
    private long _totalRequests;
    private long _totalErrors;
    private DateTime _lastRequestTime;
    private DateTime _lastDecreaseTime;

    public HostGate(string host, AdaptiveThrottleOptions options)
    {
        _host = host;
        _options = options;
        _concurrencyLimit = _options.MinConcurrency;
        _ewmaLatency = 0;
        _totalRequests = 0;
        _totalErrors = 0;
        _lastRequestTime = DateTime.UtcNow;
        _lastDecreaseTime = DateTime.MinValue;
    }

    /// <summary>
    /// Try to acquire a concurrency slot for this host. If the current number of
    /// in-flight requests is at or above the current concurrency limit the caller will
    /// be asynchronously queued until a slot becomes free.
    /// </summary>
    public async Task EnterAsync()
    {
        TaskCompletionSource<bool> waiter = null;
        lock (_lock)
        {
            if (_running < _concurrencyLimit)
            {
                _running++;
            }
            else
            {
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
            }
        }

        if (waiter != null)
        {
            await waiter.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Release a previously acquired concurrency slot and wake any queued
    /// waiters if capacity is available.
    /// </summary>
    public void Leave()
    {
        TaskCompletionSource<bool> next = null;
        lock (_lock)
        {
            _running--;
            if (_waiters.Count > 0 && _running < _concurrencyLimit)
            {
                next = _waiters.Dequeue();
                _running++;
            }
        }
        next?.SetResult(true);
    }

    /// <summary>
    /// Wait if necessary so that at least RequestSpacing has elapsed since the last
    /// request started to this host. This is used to pace requests when concurrency
    /// is low.
    /// </summary>
    public async Task WaitForSpacingAsync()
    {
        var spacing = _options.RequestSpacing;
        if (spacing <= TimeSpan.Zero)
        {
            _lastRequestTime = DateTime.UtcNow;
            return;
        }
        var now = DateTime.UtcNow;
        var nextAllowed = _lastRequestTime.Add(spacing);
        if (nextAllowed > now)
        {
            var delay = nextAllowed - now;
            await Task.Delay(delay).ConfigureAwait(false);
        }
        _lastRequestTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Update latency EWMA, total counts and potentially adjust concurrency limits
    /// after a request completes.
    /// </summary>
    public void RecordResult(long latencyMs, bool isError)
    {
        _totalRequests++;
        if (isError)
        {
            _totalErrors++;
        }
        if (_totalRequests == 1)
        {
            _ewmaLatency = latencyMs;
        }
        else
        {
            var alpha = _options.EwmaAlpha;
            _ewmaLatency = (1 - alpha) * _ewmaLatency + alpha * latencyMs;
        }
        if (_options.EnableAdaptiveThrottling)
        {
            AdaptConcurrency(isError);
        }
    }

    private void AdaptConcurrency(bool lastWasError)
    {
        var errRate = ErrorRate;
        var now = DateTime.UtcNow;
        if (lastWasError || _ewmaLatency > _options.MaxLatencyThresholdMs || errRate > _options.ErrorRateThreshold)
        {
            if (_concurrencyLimit > _options.MinConcurrency)
            {
                _concurrencyLimit--;
                _lastDecreaseTime = now;
            }
        }
        else if (_concurrencyLimit < _options.MaxConcurrency)
        {
            if (now - _lastDecreaseTime >= _options.CooldownPeriod &&
                _ewmaLatency < _options.MinLatencyThresholdMs &&
                errRate < _options.ErrorRateThreshold)
            {
                _concurrencyLimit++;
            }
        }
    }

    public double ErrorRate => _totalRequests == 0 ? 0 : (double)_totalErrors / _totalRequests;
    public DateTime LastUsed => _lastRequestTime;

    public HostGateStats ToStats()
    {
        return new HostGateStats
        {
            Host = _host,
            EwmaLatency = _ewmaLatency,
            CurrentConcurrency = _concurrencyLimit,
            ErrorRate = ErrorRate,
            TotalRequests = _totalRequests,
            TotalErrors = _totalErrors
        };
    }
}
