using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Throttling;

public class HostGate
{
    private readonly AdaptiveThrottleOptions _options;
    private readonly object _lock = new object();
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly Queue<DateTime> _errorTimestamps = new Queue<DateTime>();
    
    private double _ewmaLatency = 0;
    private DateTime _lastRequestTime = DateTime.MinValue;
    private DateTime _lastCooldownTime = DateTime.MinValue;
    private int _currentConcurrency;
    private long _totalRequests = 0;
    private long _totalErrors = 0;
    private bool _isFirstRequest = true;

    public HostGate(string host, AdaptiveThrottleOptions options)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _currentConcurrency = _options.MinConcurrency;
        _concurrencySemaphore = new SemaphoreSlim(_currentConcurrency, _currentConcurrency);
    }

    public string Host { get; }
    public double EwmaLatency => _ewmaLatency;
    public int CurrentConcurrency => _currentConcurrency;
    public double ErrorRate => CalculateErrorRate();
    public long TotalRequests => _totalRequests;
    public long TotalErrors => _totalErrors;

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _concurrencySemaphore.WaitAsync(cancellationToken);
        
        TimeSpan delayTime = TimeSpan.Zero;
        lock (_lock)
        {
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
            if (timeSinceLastRequest < _options.RequestSpacing)
            {
                delayTime = _options.RequestSpacing - timeSinceLastRequest;
            }
            
            _lastRequestTime = DateTime.UtcNow.Add(delayTime);
            _totalRequests++;
        }
        
        if (delayTime > TimeSpan.Zero)
        {
            await Task.Delay(delayTime, cancellationToken);
        }
        
        return new ConcurrencyLease(this);
    }

    public void RecordLatency(double latencyMs)
    {
        lock (_lock)
        {
            if (_isFirstRequest)
            {
                _ewmaLatency = latencyMs;
                _isFirstRequest = false;
            }
            else
            {
                _ewmaLatency = (1 - _options.EwmaAlpha) * _ewmaLatency + _options.EwmaAlpha * latencyMs;
            }
            
            AdjustConcurrency();
        }
    }

    public void RecordError()
    {
        lock (_lock)
        {
            _totalErrors++;
            _errorTimestamps.Enqueue(DateTime.UtcNow);
            
            CleanupOldErrors();
            AdjustConcurrency();
        }
    }

    private void AdjustConcurrency()
    {
        var now = DateTime.UtcNow;
        
        if (now - _lastCooldownTime < _options.CooldownPeriod)
        {
            return;
        }

        if (_totalRequests < 3)
        {
            return;
        }

        var errorRate = CalculateErrorRate();
        var shouldDecrease = _ewmaLatency > _options.MaxLatencyThresholdMs || 
                            errorRate > _options.ErrorRateThreshold;
        var shouldIncrease = _ewmaLatency < _options.MinLatencyThresholdMs && 
                            errorRate < _options.ErrorRateThreshold / 2;

        if (shouldDecrease && _currentConcurrency > _options.MinConcurrency)
        {
            DecreaseConcurrency();
            _lastCooldownTime = now;
        }
        else if (shouldIncrease && _currentConcurrency < _options.MaxConcurrency)
        {
            IncreaseConcurrency();
            _lastCooldownTime = now;
        }
    }

    private void IncreaseConcurrency()
    {
        var newConcurrency = Math.Min(_currentConcurrency + 1, _options.MaxConcurrency);
        if (newConcurrency > _currentConcurrency)
        {
            _currentConcurrency = newConcurrency;
            _concurrencySemaphore.Release();
        }
    }

    private void DecreaseConcurrency()
    {
        var newConcurrency = Math.Max(_currentConcurrency - 1, _options.MinConcurrency);
        if (newConcurrency < _currentConcurrency)
        {
            _currentConcurrency = newConcurrency;
        }
    }

    private double CalculateErrorRate()
    {
        if (_totalRequests == 0) return 0;
        
        return (double)_totalErrors / _totalRequests;
    }

    private void CleanupOldErrors()
    {
        var cutoffTime = DateTime.UtcNow.Subtract(_options.CooldownPeriod);
        
        while (_errorTimestamps.Count > 0 && _errorTimestamps.Peek() < cutoffTime)
        {
            _errorTimestamps.Dequeue();
        }
    }

    private void Release()
    {
        _concurrencySemaphore.Release();
    }

    private class ConcurrencyLease : IDisposable
    {
        private readonly HostGate _hostGate;
        private bool _disposed = false;

        public ConcurrencyLease(HostGate hostGate)
        {
            _hostGate = hostGate;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _hostGate.Release();
                _disposed = true;
            }
        }
    }
}