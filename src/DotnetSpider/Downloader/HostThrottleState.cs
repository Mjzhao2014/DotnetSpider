using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Internal state for a given host used by the adaptive throttler. Tracks EWMA latency and
/// rolling error rate over a sliding time window in order to dynamically adjust the
/// allowed level of concurrency and backoff delays for requests to that host.
/// </summary>
internal class HostThrottleState
{
    private readonly TimeSpan _window = TimeSpan.FromSeconds(30);
    private readonly double _alpha = 0.2; // smoothing for EWMA
    private readonly int _successesForPromotion = 5;
    private readonly double _highLatencyFactor = 2.0;
    private readonly object _lock = new();
    private readonly Queue<DateTime> _allRequests = new();
    private readonly Queue<DateTime> _errors = new();
    private double _ewmaLatency;
    private int _consecutiveSuccess;
    private int _maxConcurrency;
    private int _currentActive;

    public HostThrottleState(int initialConcurrency = 2)
    {
        _maxConcurrency = initialConcurrency < 1 ? 1 : initialConcurrency;
    }

    /// <summary>
    /// Current target concurrency for this host.
    /// </summary>
    public int Concurrency => Volatile.Read(ref _maxConcurrency);

    /// <summary>
    /// Acquire a slot for issuing a request against this host. This will block asynchronously
    /// until the number of active requests is below the current concurrency limit.
    /// A small random jitter is also applied to avoid lockstep behavior.
    /// </summary>
    public async Task AcquireAsync()
    {
        // Apply a small jitter before attempting to acquire to avoid lockstep.
        var delayMs = Random.Shared.Next(0, 50);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }
        while (true)
        {
            var current = Volatile.Read(ref _currentActive);
            var limit = Volatile.Read(ref _maxConcurrency);
            if (current < limit)
            {
                var prev = Interlocked.CompareExchange(ref _currentActive, current + 1, current);
                if (prev == current)
                {
                    return;
                }
            }
            // Too many in flight, back off a bit.
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Release a previously acquired slot and incorporate the outcome of the request for
    /// dynamic adjustment of concurrency.
    /// </summary>
    public void Release(bool success, int elapsedMs, int statusCode)
    {
        Interlocked.Decrement(ref _currentActive);
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _allRequests.Enqueue(now);
            while (_allRequests.Count > 0 && now - _allRequests.Peek() > _window)
            {
                _allRequests.Dequeue();
            }
            if (!success || statusCode >= 400)
            {
                _errors.Enqueue(now);
                while (_errors.Count > 0 && now - _errors.Peek() > _window)
                {
                    _errors.Dequeue();
                }
            }
            // Update EWMA latency
            if (elapsedMs > 0)
            {
                if (_ewmaLatency <= 0)
                {
                    _ewmaLatency = elapsedMs;
                }
                else
                {
                    _ewmaLatency = (1 - _alpha) * _ewmaLatency + _alpha * elapsedMs;
                }
            }
            var errorRate = _allRequests.Count == 0 ? 0.0 : (double)_errors.Count / _allRequests.Count;
            var highLatency = _ewmaLatency > 0 && elapsedMs > _ewmaLatency * _highLatencyFactor;
            if (!success || statusCode >= 400 || highLatency)
            {
                _consecutiveSuccess = 0;
                if (_maxConcurrency > 1)
                {
                    _maxConcurrency--;
                }
            }
            else
            {
                _consecutiveSuccess++;
                if (_consecutiveSuccess >= _successesForPromotion && errorRate < 0.5)
                {
                    _maxConcurrency++;
                    _consecutiveSuccess = 0;
                }
            }
        }
    }
}
