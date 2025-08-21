using System;

namespace DotnetSpider;

/// <summary>
/// Maintains per-host adaptive throttle state such as concurrency limits,
/// EWMA latency and error rate.
/// Concurrency increases additively when latency is low and errors are minimal,
/// and decreases multiplicatively when errors spike or latency rises.
/// </summary>
internal class HostGate
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly object _syncRoot = new();
    private int _currentConcurrency;
    private double _latencyEwma;
    private double _errorEwma;
    private DateTime _lastAdjustTime;

    public HostGate(AdaptiveThrottleOptions options)
    {
        _options = options;
        ConcurrencyLimit = options.MinConcurrency;
        _latencyEwma = 0;
        _errorEwma = 0;
        _lastAdjustTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Current maximum number of concurrent in-flight requests allowed.
    /// </summary>
    public int ConcurrencyLimit { get; private set; }

    /// <summary>
    /// Attempt to acquire a concurrency slot for this host.
    /// Returns true if CurrentConcurrency &lt; ConcurrencyLimit and increments current concurrency.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_syncRoot)
        {
            if (_currentConcurrency >= ConcurrencyLimit)
            {
                return false;
            }
            _currentConcurrency++;
            return true;
        }
    }

    /// <summary>
    /// Release an in-flight request and update EWMA latency/error statistics.
    /// </summary>
    public void Release(bool success, int latencyMilliseconds)
    {
        lock (_syncRoot)
        {
            if (_currentConcurrency > 0)
            {
                _currentConcurrency--;
            }

            // Update latency and error rate.
            if (success)
            {
                _latencyEwma = _latencyEwma <= 0 ? latencyMilliseconds :
                    (1 - _options.EwmaAlpha) * _latencyEwma + _options.EwmaAlpha * latencyMilliseconds;
                _errorEwma = (1 - _options.EwmaAlpha) * _errorEwma;
            }
            else
            {
                // On error, update error probability EWMA and still update latency EWMA.
                _errorEwma = (1 - _options.EwmaAlpha) * _errorEwma + _options.EwmaAlpha;
                _latencyEwma = _latencyEwma <= 0 ? latencyMilliseconds :
                    (1 - _options.EwmaAlpha) * _latencyEwma + _options.EwmaAlpha * latencyMilliseconds;
            }

            AdjustConcurrencyIfNeeded();
        }
    }

    /// <summary>
    /// Called periodically to promote or demote concurrency based on EWMA signals.
    /// Uses additive increase and multiplicative decrease.
    /// </summary>
    private void AdjustConcurrencyIfNeeded()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastAdjustTime).TotalMilliseconds < _options.CooldownMilliseconds)
        {
            return;
        }

        _lastAdjustTime = now;
        // Demote if error probability is high or latency crosses high threshold.
        if (_errorEwma > _options.ErrorRateThreshold ||
            (_latencyEwma > 0 && _latencyEwma > _options.LatencyHighThreshold))
        {
            // Multiplicative decrease with floor at MinConcurrency
            var decreased = Math.Max(_options.MinConcurrency, (int)Math.Ceiling(ConcurrencyLimit / 2.0));
            ConcurrencyLimit = decreased;
        }
        else if (_latencyEwma > 0 && _latencyEwma < _options.LatencyLowThreshold && ConcurrencyLimit < _options.MaxConcurrency)
        {
            // Additive increase when latency is low and concurrency not at max.
            ConcurrencyLimit++;
        }
        // else: maintain current concurrency.
    }
}
