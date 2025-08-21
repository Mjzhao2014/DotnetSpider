using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Throttling;

public class HostThrottler : IHostThrottler
{
    private readonly ConcurrentDictionary<string, HostThrottleState> _hostStates = new();
    private readonly ILogger<HostThrottler> _logger;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _hostInactivityThreshold = TimeSpan.FromMinutes(10);

    public HostThrottler(ILogger<HostThrottler> logger)
    {
        _logger = logger;
        _cleanupTimer = new Timer(CleanupInactiveHosts, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public async Task<IDisposable> AcquireAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(host))
            throw new ArgumentException("Host cannot be null or empty", nameof(host));

        var hostState = _hostStates.GetOrAdd(host, _ => new HostThrottleState());
        
        hostState.RecordRequest();
        
        var delay = hostState.CalculateDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        await hostState.ConcurrencySemaphore.WaitAsync(cancellationToken);

        return new ThrottleToken(hostState, _logger);
    }

    public void RecordSuccess(string host, TimeSpan latency)
    {
        if (_hostStates.TryGetValue(host, out var hostState))
        {
            hostState.RecordLatency(latency);
            hostState.RecordSuccess();
            
            _logger.LogDebug("Host {Host}: Success recorded. Latency: {Latency}ms, Concurrency: {Concurrency}, Error Rate: {ErrorRate:P2}",
                host, latency.TotalMilliseconds, hostState.CurrentConcurrency, hostState.ErrorRate);
        }
    }

    public void RecordError(string host, TimeSpan latency)
    {
        if (_hostStates.TryGetValue(host, out var hostState))
        {
            hostState.RecordLatency(latency);
            hostState.RecordError();
            
            _logger.LogWarning("Host {Host}: Error recorded. Latency: {Latency}ms, Concurrency: {Concurrency}, Error Rate: {ErrorRate:P2}",
                host, latency.TotalMilliseconds, hostState.CurrentConcurrency, hostState.ErrorRate);
        }
    }

    public HostMetrics GetMetrics(string host)
    {
        if (_hostStates.TryGetValue(host, out var hostState))
        {
            return new HostMetrics
            {
                CurrentConcurrency = hostState.CurrentConcurrency,
                AverageLatency = hostState.AverageLatency,
                ErrorRate = hostState.ErrorRate,
                TimeSinceLastRequest = hostState.TimeSinceLastRequest
            };
        }

        return new HostMetrics();
    }

    private void CleanupInactiveHosts(object state)
    {
        var hostsToRemove = new List<string>();
        
        foreach (var kvp in _hostStates)
        {
            if (kvp.Value.TimeSinceLastRequest > _hostInactivityThreshold)
            {
                hostsToRemove.Add(kvp.Key);
            }
        }

        foreach (var host in hostsToRemove)
        {
            if (_hostStates.TryRemove(host, out var hostState))
            {
                hostState.Dispose();
                _logger.LogDebug("Removed inactive host state for {Host}", host);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        
        foreach (var hostState in _hostStates.Values)
        {
            hostState.Dispose();
        }
        
        _hostStates.Clear();
    }
}

internal class ThrottleToken : IDisposable
{
    private readonly HostThrottleState _hostState;
    private readonly ILogger _logger;
    private bool _disposed;

    public ThrottleToken(HostThrottleState hostState, ILogger logger)
    {
        _hostState = hostState;
        _logger = logger;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _hostState.ConcurrencySemaphore.Release();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error releasing concurrency semaphore");
            }
            _disposed = true;
        }
    }
}