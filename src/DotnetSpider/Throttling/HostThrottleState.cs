using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DotnetSpider.Throttling;

internal class HostThrottleState
{
    private readonly object _lock = new();
    private double _ewmaLatency = 100.0;
    private int _errorCount;
    private int _successCount;
    private int _totalRequests;
    private DateTime _lastRequestTime = DateTime.UtcNow;
    private DateTime _errorWindowStart = DateTime.UtcNow;
    
    private const double EwmaAlpha = 0.2;
    private const int ErrorWindowSeconds = 60;
    private const int MinConcurrency = 1;
    private const int MaxConcurrency = 32;
    private const double LatencyThresholdMultiplier = 3.0;
    private const double ErrorRateThreshold = 0.1;

    public SemaphoreSlim ConcurrencySemaphore { get; private set; }
    public int CurrentConcurrency { get; private set; } = 2;
    public double AverageLatency => _ewmaLatency;
    public double ErrorRate => GetCurrentErrorRate();
    public DateTime LastRequestTime => _lastRequestTime;
    public TimeSpan TimeSinceLastRequest => DateTime.UtcNow - _lastRequestTime;
    
    public HostThrottleState()
    {
        ConcurrencySemaphore = new SemaphoreSlim(CurrentConcurrency, CurrentConcurrency);
    }

    public void RecordRequest()
    {
        lock (_lock)
        {
            _lastRequestTime = DateTime.UtcNow;
            _totalRequests++;
            
            CleanupOldErrors();
        }
    }

    public void RecordLatency(TimeSpan latency)
    {
        lock (_lock)
        {
            var latencyMs = latency.TotalMilliseconds;
            _ewmaLatency = (1 - EwmaAlpha) * _ewmaLatency + EwmaAlpha * latencyMs;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _successCount++;
            
            if (ShouldIncreaseCapacity())
            {
                IncreaseCapacity();
            }
        }
    }

    public void RecordError()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _errorWindowStart > TimeSpan.FromSeconds(ErrorWindowSeconds))
            {
                _errorCount = 0;
                _errorWindowStart = now;
            }
            
            _errorCount++;
            
            if (ShouldDecreaseCapacity())
            {
                DecreaseCapacity();
            }
        }
    }

    public TimeSpan CalculateDelay()
    {
        lock (_lock)
        {
            var baseDelay = TimeSpan.FromMilliseconds(50);
            var errorRate = GetCurrentErrorRate();
            
            if (errorRate > ErrorRateThreshold)
            {
                baseDelay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * (1 + errorRate * 5));
            }
            
            if (_ewmaLatency > 500)
            {
                var latencyMultiplier = Math.Min(5.0, _ewmaLatency / 200.0);
                baseDelay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * latencyMultiplier);
            }
            
            var jitter = Random.Shared.NextDouble() * 0.3 + 0.85;
            return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * jitter);
        }
    }

    private double GetCurrentErrorRate()
    {
        CleanupOldErrors();
        
        if (_totalRequests == 0) return 0.0;
        
        var recentRequests = Math.Max(100, Math.Min(_totalRequests, 1000));
        return (double)_errorCount / recentRequests;
    }

    private void CleanupOldErrors()
    {
        var now = DateTime.UtcNow;
        if (now - _errorWindowStart > TimeSpan.FromSeconds(ErrorWindowSeconds))
        {
            _errorCount = 0;
            _errorWindowStart = now;
        }
    }

    private bool ShouldIncreaseCapacity()
    {
        if (CurrentConcurrency >= MaxConcurrency) return false;
        if (_successCount < 10) return false;
        if (GetCurrentErrorRate() > ErrorRateThreshold / 2) return false;
        if (_ewmaLatency > 300) return false;
        
        return _successCount >= Math.Max(5, CurrentConcurrency * 3);
    }

    private bool ShouldDecreaseCapacity()
    {
        if (CurrentConcurrency <= MinConcurrency) return false;
        
        var errorRate = GetCurrentErrorRate();
        return errorRate > ErrorRateThreshold || _ewmaLatency > 1000;
    }

    private void IncreaseCapacity()
    {
        if (CurrentConcurrency >= MaxConcurrency) return;
        
        var oldSemaphore = ConcurrencySemaphore;
        CurrentConcurrency = Math.Min(MaxConcurrency, CurrentConcurrency + 1);
        ConcurrencySemaphore = new SemaphoreSlim(CurrentConcurrency, CurrentConcurrency);
        
        oldSemaphore.Dispose();
        _successCount = 0;
    }

    private void DecreaseCapacity()
    {
        if (CurrentConcurrency <= MinConcurrency) return;
        
        var oldSemaphore = ConcurrencySemaphore;
        CurrentConcurrency = Math.Max(MinConcurrency, CurrentConcurrency - 1);
        ConcurrencySemaphore = new SemaphoreSlim(CurrentConcurrency, CurrentConcurrency);
        
        oldSemaphore.Dispose();
        _successCount = 0;
        _errorCount = Math.Max(0, _errorCount - 1);
    }

    public void Dispose()
    {
        ConcurrencySemaphore?.Dispose();
    }
}