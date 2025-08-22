using System;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Throttling;
using Xunit;

namespace DotnetSpider.Tests.Throttling;

public class AdaptiveThrottleManagerTests : IDisposable
{
    private readonly AdaptiveThrottleManager _manager;
    private readonly AdaptiveThrottleOptions _options;

    public AdaptiveThrottleManagerTests()
    {
        _options = new AdaptiveThrottleOptions();
        _manager = new AdaptiveThrottleManager(_options);
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AdaptiveThrottleManager(null));
    }

    [Fact]
    public void GetHostGate_ValidHost_ReturnsHostGate()
    {
        var hostGate = _manager.GetHostGate("example.com");
        
        Assert.NotNull(hostGate);
        Assert.Equal("example.com", hostGate.Host);
    }

    [Fact]
    public void GetHostGate_NullHost_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _manager.GetHostGate(null));
    }

    [Fact]
    public void GetHostGate_EmptyHost_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _manager.GetHostGate(""));
    }

    [Fact]
    public void GetHostGate_SameHost_ReturnsSameInstance()
    {
        var hostGate1 = _manager.GetHostGate("example.com");
        var hostGate2 = _manager.GetHostGate("example.com");
        
        Assert.Same(hostGate1, hostGate2);
    }

    [Fact]
    public void GetHostGate_DifferentHosts_ReturnsDifferentInstances()
    {
        var hostGate1 = _manager.GetHostGate("example.com");
        var hostGate2 = _manager.GetHostGate("google.com");
        
        Assert.NotSame(hostGate1, hostGate2);
        Assert.Equal("example.com", hostGate1.Host);
        Assert.Equal("google.com", hostGate2.Host);
    }

    [Fact]
    public async Task AcquireAsync_ValidHost_ReturnsDisposable()
    {
        using var lease = await _manager.AcquireAsync("example.com");
        
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_NullHost_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.AcquireAsync(null));
    }

    [Fact]
    public async Task AcquireAsync_CancellationToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _manager.AcquireAsync("example.com", cts.Token));
    }

    [Fact]
    public void RecordLatency_ValidHost_UpdatesHostGateLatency()
    {
        _manager.RecordLatency("example.com", 100);
        
        var hostGate = _manager.GetHostGate("example.com");
        Assert.Equal(100, hostGate.EwmaLatency);
    }

    [Fact]
    public void RecordLatency_NullHost_DoesNotThrow()
    {
        var exception = Record.Exception(() => _manager.RecordLatency(null, 100));
        Assert.Null(exception);
    }

    [Fact]
    public void RecordLatency_EmptyHost_DoesNotThrow()
    {
        var exception = Record.Exception(() => _manager.RecordLatency("", 100));
        Assert.Null(exception);
    }

    [Fact]
    public void RecordError_ValidHost_UpdatesHostGateErrorCount()
    {
        _manager.RecordError("example.com");
        
        var hostGate = _manager.GetHostGate("example.com");
        Assert.Equal(1, hostGate.TotalErrors);
    }

    [Fact]
    public void RecordError_NullHost_DoesNotThrow()
    {
        var exception = Record.Exception(() => _manager.RecordError(null));
        Assert.Null(exception);
    }

    [Fact]
    public void RecordError_EmptyHost_DoesNotThrow()
    {
        var exception = Record.Exception(() => _manager.RecordError(""));
        Assert.Null(exception);
    }

    [Fact]
    public void HostIsolation_DifferentHosts_IndependentLatencyTracking()
    {
        _manager.RecordLatency("example.com", 100);
        _manager.RecordLatency("google.com", 500);
        
        var hostGate1 = _manager.GetHostGate("example.com");
        var hostGate2 = _manager.GetHostGate("google.com");
        
        Assert.Equal(100, hostGate1.EwmaLatency);
        Assert.Equal(500, hostGate2.EwmaLatency);
    }

    [Fact]
    public void HostIsolation_DifferentHosts_IndependentErrorTracking()
    {
        _manager.RecordError("example.com");
        _manager.RecordError("example.com");
        _manager.RecordError("google.com");
        
        var hostGate1 = _manager.GetHostGate("example.com");
        var hostGate2 = _manager.GetHostGate("google.com");
        
        Assert.Equal(2, hostGate1.TotalErrors);
        Assert.Equal(1, hostGate2.TotalErrors);
    }

    [Fact]
    public void HostIsolation_DifferentHosts_IndependentConcurrencyAdjustment()
    {
        var options = new AdaptiveThrottleOptions 
        { 
            MinConcurrency = 1,
            MaxConcurrency = 10,
            MaxLatencyThresholdMs = 200,
            CooldownPeriod = TimeSpan.Zero
        };
        using var manager = new AdaptiveThrottleManager(options);
        
        manager.RecordLatency("example.com", 50);
        manager.RecordLatency("google.com", 1000);
        
        var hostGate1 = manager.GetHostGate("example.com");
        var hostGate2 = manager.GetHostGate("google.com");
        
        Assert.True(hostGate1.CurrentConcurrency >= hostGate2.CurrentConcurrency);
    }

    [Fact]
    public void GetHostCount_InitiallyZero()
    {
        Assert.Equal(0, _manager.GetHostCount());
    }

    [Fact]
    public void GetHostCount_AfterAddingHosts_ReturnsCorrectCount()
    {
        _manager.GetHostGate("example.com");
        Assert.Equal(1, _manager.GetHostCount());
        
        _manager.GetHostGate("google.com");
        Assert.Equal(2, _manager.GetHostCount());
        
        _manager.GetHostGate("example.com");
        Assert.Equal(2, _manager.GetHostCount());
    }

    [Fact]
    public void GetHostStats_NonExistentHost_ReturnsNull()
    {
        var stats = _manager.GetHostStats("nonexistent.com");
        Assert.Null(stats);
    }

    [Fact]
    public void GetHostStats_ExistingHost_ReturnsCorrectStats()
    {
        _manager.RecordLatency("example.com", 100);
        _manager.RecordError("example.com");
        
        var stats = _manager.GetHostStats("example.com");
        
        Assert.NotNull(stats);
        Assert.Equal("example.com", stats.Host);
        Assert.Equal(100, stats.EwmaLatency);
        Assert.Equal(1, stats.TotalErrors);
    }

    [Fact]
    public void GetAllHostStats_InitiallyEmpty()
    {
        var allStats = _manager.GetAllHostStats();
        Assert.Empty(allStats);
    }

    [Fact]
    public void GetAllHostStats_WithHosts_ReturnsAllStats()
    {
        _manager.RecordLatency("example.com", 100);
        _manager.RecordLatency("google.com", 200);
        
        var allStats = _manager.GetAllHostStats();
        
        Assert.Equal(2, allStats.Length);
        
        var exampleStats = Array.Find(allStats, s => s.Host == "example.com");
        var googleStats = Array.Find(allStats, s => s.Host == "google.com");
        
        Assert.NotNull(exampleStats);
        Assert.NotNull(googleStats);
        Assert.Equal(100, exampleStats.EwmaLatency);
        Assert.Equal(200, googleStats.EwmaLatency);
    }

    [Fact]
    public async Task ConcurrentAccess_MultipleHosts_ThreadSafe()
    {
        var hosts = new[] { "host1.com", "host2.com", "host3.com" };
        var tasks = new Task[30];
        
        for (int i = 0; i < tasks.Length; i++)
        {
            var hostIndex = i % hosts.Length;
            var host = hosts[hostIndex];
            
            tasks[i] = Task.Run(async () =>
            {
                using (await _manager.AcquireAsync(host))
                {
                    _manager.RecordLatency(host, Random.Shared.Next(50, 500));
                    if (Random.Shared.NextDouble() < 0.1)
                    {
                        _manager.RecordError(host);
                    }
                }
            });
        }
        
        await Task.WhenAll(tasks);
        
        Assert.Equal(3, _manager.GetHostCount());
        
        foreach (var host in hosts)
        {
            var stats = _manager.GetHostStats(host);
            Assert.NotNull(stats);
            Assert.True(stats.TotalRequests > 0);
        }
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }
}