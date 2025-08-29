using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Tracks per-host throttling state such as EWMA latency, error counts, and current
/// concurrency limit. Provides gating of concurrency and request pacing for a host.
/// </summary>
internal class HostGate
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly string _host;
    // Atomically incremented number of in-flight requests for this host.
    private int _inflight;
    // Concurrency limit for this host.
    private int _concurrencyLimit;
    // Last time we started a request (used for spacing).
    private DateTime _nextRequestAllowed = DateTime.MinValue;
    // Last time we adjusted concurrency; used for cooldown.
    private DateTime _lastAdjustTime = DateTime.MinValue;
    private double _ewmaLatency;
    private long _totalRequests;
    private long _totalErrors;
    private readonly object _syncRoot = new();

    public HostGate(string host, AdaptiveThrottleOptions options)
    {
        _host = host;
        _options = options;
        _concurrencyLimit = options.MinConcurrency <= 0 ? 1 : options.MinConcurrency;
    }

    /// <summary>
    /// Waits for any required inter-request spacing and an available concurrency slot for this host.
    /// Returns an IDisposable that must be disposed to release the concurrency slot.
    /// </summary>
    public async Task<IDisposable> AcquireSlotAsync()
    {
        // Enforce request pacing if configured
        TimeSpan delay = TimeSpan.Zero;
        lock (_syncRoot)
        {
            var now = DateTime.UtcNow;
            if (_options.RequestSpacing > TimeSpan.Zero)
            {
                if (now < _nextRequestAllowed)
                {
                    delay = _nextRequestAllowed - now;
                }
                _nextRequestAllowed = (_nextRequestAllowed > now ? _nextRequestAllowed : now) + _options.RequestSpacing;
            }
        }
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }
        // Concurrency gating using spin loop and atomic counter
        while (true)
        {
            var current = Interlocked.Increment(ref _inflight);
            if (current <= _concurrencyLimit)
            {
                break;
            }
            Interlocked.Decrement(ref _inflight);
            // Give up the thread for a bit before retrying
            await Task.Delay(10);
        }
        return new ReleaseHandle(this);
    }

    /// <summary>
    /// Update feedback metrics for a completed request.
    /// </summary>
    public void MarkResult(double latencyMs, bool isError)
    {
        lock (_syncRoot)
        {
            _totalRequests++;
            if (isError)
            {
                _totalErrors++;
            }
            // Update EWMA latency
            if (latencyMs > 0)
            {
                if (_ewmaLatency <= 0)
                {
                    _ewmaLatency = latencyMs;
                }
                else
                {
                    _ewmaLatency = (1 - _options.EwmaAlpha) * _ewmaLatency + _options.EwmaAlpha * latencyMs;
                }
            }
            MaybeAdjustConcurrency();
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
                ErrorRate = _totalRequests == 0 ? 0 : (double)_totalErrors / _totalRequests,
                TotalRequests = _totalRequests,
                TotalErrors = _totalErrors
            };
        }
    }

    private void MaybeAdjustConcurrency()
    {
        if (_options.MinConcurrency <= 0 && _options.MaxConcurrency <= 0)
        {
            return; // nothing to adjust
        }
        var now = DateTime.UtcNow;
        if ((now - _lastAdjustTime) < _options.CooldownPeriod)
        {
            return;
        }
        var errorRate = _totalRequests == 0 ? 0 : (double)_totalErrors / _totalRequests;
        if (_ewmaLatency > _options.MaxLatencyThresholdMs || errorRate > _options.ErrorRateThreshold)
        {
            if (_concurrencyLimit > _options.MinConcurrency)
            {
                _concurrencyLimit--;
                _lastAdjustTime = now;
            }
        }
        else if (_ewmaLatency > 0 && _ewmaLatency < _options.MinLatencyThresholdMs && errorRate < _options.ErrorRateThreshold)
        {
            if (_concurrencyLimit < _options.MaxConcurrency)
            {
                _concurrencyLimit++;
                _lastAdjustTime = now;
            }
        }
    }

    private sealed class ReleaseHandle : IDisposable
    {
        private readonly HostGate _gate;
        public ReleaseHandle(HostGate gate)
        {
            _gate = gate;
        }
        public void Dispose()
        {
            Interlocked.Decrement(ref _gate._inflight);
        }
    }
}
