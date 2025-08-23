using System;
using System.Threading;
using System.Threading.Tasks;

using System.Runtime.CompilerServices;

namespace DotnetSpider.Downloader;

/// <summary>
/// Maintains per-host throttling state such as adaptive concurrency
/// limits, EWMA latency measurements, error rates and spacing of requests.
/// Acts as an asynchronous gate that callers must acquire before
/// issuing a request against a given host.
/// </summary>
/// <summary>
/// HostGate controls concurrency for a single host.
/// </summary>
public class HostGate
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly SemaphoreSlim _semaphore;
    private double _ewmaLatency;
    private int _sinceAdjustmentRequests;
    private int _sinceAdjustmentErrors;
    private DateTime _lastAdjustment;
    private DateTime _lastRequestStart;
    private int _concurrencyLimit;

    /// <summary>
    /// Expose current concurrency limit (for testing/inspection only).
    /// </summary>
    internal int ConcurrencyLimit => _concurrencyLimit;

    public HostGate(AdaptiveThrottleOptions options)
    {
        _options = options;
        _concurrencyLimit = Math.Max(1, options.MinConcurrency);
        // create semaphore with initial concurrency limit
        _semaphore = new SemaphoreSlim(_concurrencyLimit, int.MaxValue);
        _lastAdjustment = DateTime.UtcNow;
        _lastRequestStart = DateTime.MinValue;
    }

    /// <summary>
    /// Asynchronously waits for a concurrency slot and enforces optional
    /// request spacing for this host. Returns a disposable handle to
    /// release the slot when the caller is finished with request handling.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        // enforce request spacing when concurrency of 1:
        if (_options.RequestSpacing > TimeSpan.Zero && _concurrencyLimit <= 1)
        {
            var wait = _options.RequestSpacing - (DateTime.UtcNow - _lastRequestStart);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
        _lastRequestStart = DateTime.UtcNow;
        return new ReleaseHandle(this);
    }

    private void Release()
    {
        _semaphore.Release();
    }

    /// <summary>
    /// Record the outcome of a request against this host to update
    /// latency and error EWMA and adjust concurrency if necessary.
    /// </summary>
    public void RecordResult(long latencyMilliseconds, bool isError)
    {
        if (_sinceAdjustmentRequests == 0)
        {
            // initialize EWMA on first sample
            _ewmaLatency = latencyMilliseconds;
        }
        else
        {
            _ewmaLatency = (1 - _options.EwmaAlpha) * _ewmaLatency + _options.EwmaAlpha * latencyMilliseconds;
        }

        _sinceAdjustmentRequests++;
        if (isError)
        {
            _sinceAdjustmentErrors++;
        }

        var now = DateTime.UtcNow;
        if (now - _lastAdjustment < _options.CooldownPeriod)
        {
            return;
        }
        // compute error rate over requests since last adjustment
        double errorRate = _sinceAdjustmentRequests > 0 ? (double)_sinceAdjustmentErrors / _sinceAdjustmentRequests : 0;
        bool degrade = errorRate > _options.ErrorRateThreshold || latencyMilliseconds > _options.MaxLatencyThresholdMs || _ewmaLatency > _options.MaxLatencyThresholdMs;
        bool improve = errorRate <= _options.ErrorRateThreshold && _ewmaLatency < _options.MinLatencyThresholdMs;

        if (degrade && _concurrencyLimit > _options.MinConcurrency)
        {
            _concurrencyLimit--;
            // if we decreased concurrency limit, we can't shrink the semaphore's count directly.
            // Existing acquisitions will continue, new acquires will eventually block when concurrency in-flight equals new limit.
        }
        else if (improve && _concurrencyLimit < _options.MaxConcurrency)
        {
            _concurrencyLimit++;
            _semaphore.Release();
        }

        _sinceAdjustmentRequests = 0;
        _sinceAdjustmentErrors = 0;
        _lastAdjustment = now;
    }

    private sealed class ReleaseHandle : IDisposable
    {
        private readonly HostGate _gate;
        private bool _disposed;
        public ReleaseHandle(HostGate gate)
        {
            _gate = gate;
        }
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _gate.Release();
            }
        }
    }
}
