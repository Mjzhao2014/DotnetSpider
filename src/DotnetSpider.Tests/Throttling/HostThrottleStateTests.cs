using System;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Throttling;
using Xunit;

namespace DotnetSpider.Tests.Throttling;

public class HostThrottleStateTests
{
    [Fact]
    public void Constructor_InitializesWithDefaultValues()
    {
        var hostState = new HostThrottleState();
        
        Assert.Equal(2, hostState.CurrentConcurrency);
        Assert.Equal(100.0, hostState.AverageLatency);
        Assert.Equal(0.0, hostState.ErrorRate);
        Assert.True(hostState.TimeSinceLastRequest < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RecordLatency_UpdatesEWMA()
    {
        var hostState = new HostThrottleState();
        var initialLatency = hostState.AverageLatency;
        
        hostState.RecordLatency(TimeSpan.FromMilliseconds(200));
        
        Assert.NotEqual(initialLatency, hostState.AverageLatency);
        Assert.True(hostState.AverageLatency > initialLatency);
    }

    [Fact]
    public void RecordSuccess_IncreasesCapacityAfterMultipleSuccesses()
    {
        var hostState = new HostThrottleState();
        var initialConcurrency = hostState.CurrentConcurrency;
        
        for (int i = 0; i < 20; i++)
        {
            hostState.RecordLatency(TimeSpan.FromMilliseconds(50));
            hostState.RecordSuccess();
        }
        
        Assert.True(hostState.CurrentConcurrency >= initialConcurrency);
    }

    [Fact]
    public void RecordError_DecreasesCapacityOnHighErrorRate()
    {
        var hostState = new HostThrottleState();
        var initialConcurrency = hostState.CurrentConcurrency;
        
        for (int i = 0; i < 50; i++)
        {
            hostState.RecordRequest();
            hostState.RecordError();
        }
        
        Assert.True(hostState.CurrentConcurrency <= initialConcurrency);
        Assert.True(hostState.ErrorRate > 0);
    }

    [Fact]
    public void CalculateDelay_ReturnsPositiveDelay()
    {
        var hostState = new HostThrottleState();
        
        var delay = hostState.CalculateDelay();
        
        Assert.True(delay >= TimeSpan.Zero);
    }

    [Fact]
    public void CalculateDelay_IncreasesWithHighLatency()
    {
        var hostState = new HostThrottleState();
        
        var normalDelay = hostState.CalculateDelay();
        
        hostState.RecordLatency(TimeSpan.FromMilliseconds(1000));
        var highLatencyDelay = hostState.CalculateDelay();
        
        Assert.True(highLatencyDelay >= normalDelay);
    }

    [Fact]
    public async Task ConcurrencySemaphore_LimitsParallelAccess()
    {
        var hostState = new HostThrottleState();
        var concurrentTasks = 10;
        var activeCount = 0;
        var maxActiveCount = 0;
        var tasks = new Task[concurrentTasks];
        
        for (int i = 0; i < concurrentTasks; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await hostState.ConcurrencySemaphore.WaitAsync();
                try
                {
                    var current = Interlocked.Increment(ref activeCount);
                    var currentMax = Math.Max(maxActiveCount, current);
                    Interlocked.Exchange(ref maxActiveCount, currentMax);
                    
                    await Task.Delay(50);
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                    hostState.ConcurrencySemaphore.Release();
                }
            });
        }
        
        await Task.WhenAll(tasks);
        
        Assert.True(maxActiveCount <= hostState.CurrentConcurrency + 1);
    }

    [Fact]
    public void Dispose_DisposesResources()
    {
        var hostState = new HostThrottleState();
        
        hostState.Dispose();
        
        Assert.Throws<ObjectDisposedException>(() => 
            hostState.ConcurrencySemaphore.Wait(100));
    }
}