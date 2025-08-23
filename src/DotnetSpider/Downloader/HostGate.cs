using System;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Internal gate controlling concurrency and pacing for a single host.
/// Each request must acquire the gate before dispatching; upon completion,
/// results are fed back to update EWMA latency and error counts which adjust
/// allowed concurrency up or down.
/// </summary>
internal class HostGate
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly object _lock = new();
    private int _concurrencyLimit;
    private int _activeCount;
    private double _ewmaLatency;
    private long _totalRequests;
    private long _totalErrors;
    private DateTime _lastAdjustment;
    private DateTime _nextRequestAllowed;

    public HostGate(string host, AdaptiveThrottleOptions options)
    {
        Host = host;
        _options = options;
        _concurrencyLimit = Math.Max(options.MinConcurrency, 1);
        _lastAdjustment = DateTime.UtcNow;
    }

    public string Host { get; }

    /// <summary>
    /// Returns time gate was last interacted with for cleanup.
    /// </summary>
    public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// Acquire an execution slot. Async waits while concurrency limit is reached
    /// or spacing interval has not elapsed.
    /// </summary>
    public async Task<IDisposable> AcquireAsync()
    {
        while (true)
        {
            int delayMs;
            lock (_lock)
            {
                LastUsed = DateTime.UtcNow;
                if (_activeCount < _concurrencyLimit && DateTime.UtcNow >= _nextRequestAllowed)
                {
                    _activeCount++;
                    _nextRequestAllowed = DateTime.UtcNow + _options.RequestSpacing;
                    return new ReleaseHandle(this);
                }
                // compute how long to wait either for concurrency slot or spacing window
                var waitForSpacing = _nextRequestAllowed > DateTime.UtcNow
                    ? (int)(_nextRequestAllowed - DateTime.UtcNow).TotalMilliseconds
                    : 0;
                delayMs = Math.Max(waitForSpacing, 10);
            }
            await Task.Delay(delayMs);
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            if (_activeCount > 0)
            {
                _activeCount--;
            }
        }
    }

    private class ReleaseHandle : IDisposable
    {
        private readonly HostGate _gate;
        public ReleaseHandle(HostGate gate) { _gate = gate; }
        public void Dispose() => _gate.Release();
    }

    /// <summary>
    /// Called after each response to update EWMA latency and error counters
    /// and adjust concurrency up/down.
    /// </summary>
    public void MarkResult(TimeSpan latency, bool success)
    {
        lock (_lock)
        {
            LastUsed = DateTime.UtcNow;
            _totalRequests++;
            if (!success) _totalErrors++;
            var ms = latency.TotalMilliseconds;
            if (ms > 0)
            {
                if (_ewmaLatency == 0)
                {
                    _ewmaLatency = ms;
                }
                else
                {
                    _ewmaLatency = (1 - _options.EwmaAlpha) * _ewmaLatency + _options.EwmaAlpha * ms;
                }
            }
            // adjust concurrency if cooldown period elapsed
            if (DateTime.UtcNow - _lastAdjustment >= _options.CooldownPeriod)
            {
                var errorRate = _totalRequests > 0 ? (_totalErrors / (double)_totalRequests) : 0;
                bool shouldScaleDown = errorRate > _options.ErrorRateThreshold || (_ewmaLatency > _options.MaxLatencyThresholdMs && _options.MaxLatencyThresholdMs > 0);
                bool shouldScaleUp = errorRate <= _options.ErrorRateThreshold && _ewmaLatency > 0 && _ewmaLatency < _options.MinLatencyThresholdMs;
                if (shouldScaleDown)
                {
                    _concurrencyLimit = Math.Max(_options.MinConcurrency, _concurrencyLimit - 1);
                    _lastAdjustment = DateTime.UtcNow;
                }
                else if (shouldScaleUp)
                {
                    _concurrencyLimit = Math.Min(_options.MaxConcurrency, _concurrencyLimit + 1);
                    _lastAdjustment = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>
    /// Snapshot current stats for diagnosis.
    /// </summary>
    public HostGateStats GetStats()
    {
        lock (_lock)
        {
            var errorRate = _totalRequests > 0 ? (_totalErrors / (double)_totalRequests) : 0;
            return new HostGateStats
            {
                Host = Host,
                EwmaLatency = _ewmaLatency,
                CurrentConcurrency = _concurrencyLimit,
                ErrorRate = errorRate,
                TotalRequests = _totalRequests,
                TotalErrors = _totalErrors
            };
        }
    }
}
