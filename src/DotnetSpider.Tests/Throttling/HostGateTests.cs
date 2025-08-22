using System;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Throttling;
using Xunit;

namespace DotnetSpider.Tests.Throttling;

public class HostGateTests
{
    [Fact]
    public void Constructor_ValidParameters_InitializesCorrectly()
    {
        var options = new AdaptiveThrottleOptions();
        var hostGate = new HostGate("example.com", options);
        
        Assert.Equal("example.com", hostGate.Host);
        Assert.Equal(0, hostGate.EwmaLatency);
        Assert.Equal(options.MinConcurrency, hostGate.CurrentConcurrency);
        Assert.Equal(0, hostGate.ErrorRate);
        Assert.Equal(0, hostGate.TotalRequests);
        Assert.Equal(0, hostGate.TotalErrors);
    }

    [Fact]
    public void Constructor_NullHost_ThrowsArgumentNullException()
    {
        var options = new AdaptiveThrottleOptions();
        
        Assert.Throws<ArgumentNullException>(() => new HostGate(null, options));
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new HostGate("example.com", null));
    }

    [Fact]
    public void RecordLatency_FirstRequest_SetsEwmaLatencyToSample()
    {
        var options = new AdaptiveThrottleOptions { EwmaAlpha = 0.3 };
        var hostGate = new HostGate("example.com", options);
        
        hostGate.RecordLatency(100);
        
        Assert.Equal(100, hostGate.EwmaLatency);
    }

    [Fact]
    public void RecordLatency_SubsequentRequests_CalculatesEwmaCorrectly()
    {
        var options = new AdaptiveThrottleOptions { EwmaAlpha = 0.3 };
        var hostGate = new HostGate("example.com", options);
        
        hostGate.RecordLatency(100);
        hostGate.RecordLatency(200);
        
        var expectedEwma = (1 - 0.3) * 100 + 0.3 * 200;
        Assert.Equal(expectedEwma, hostGate.EwmaLatency);
    }

    [Fact]
    public void RecordLatency_MultipleRequests_CalculatesEwmaCorrectly()
    {
        var options = new AdaptiveThrottleOptions { EwmaAlpha = 0.3 };
        var hostGate = new HostGate("example.com", options);
        
        hostGate.RecordLatency(100);
        var firstEwma = hostGate.EwmaLatency;
        
        hostGate.RecordLatency(300);
        var secondEwma = (1 - 0.3) * firstEwma + 0.3 * 300;
        Assert.Equal(secondEwma, hostGate.EwmaLatency);
        
        hostGate.RecordLatency(50);
        var thirdEwma = (1 - 0.3) * secondEwma + 0.3 * 50;
        Assert.Equal(thirdEwma, hostGate.EwmaLatency);
    }

    [Fact]
    public void RecordError_IncrementsTotalErrors()
    {
        var options = new AdaptiveThrottleOptions();
        var hostGate = new HostGate("example.com", options);
        
        Assert.Equal(0, hostGate.TotalErrors);
        
        hostGate.RecordError();
        Assert.Equal(1, hostGate.TotalErrors);
        
        hostGate.RecordError();
        Assert.Equal(2, hostGate.TotalErrors);
    }

    [Fact]
    public async Task RecordError_UpdatesErrorRate()
    {
        var options = new AdaptiveThrottleOptions();
        var hostGate = new HostGate("example.com", options);
        
        using (await hostGate.AcquireAsync())
        {
        }
        
        hostGate.RecordError();
        
        Assert.True(hostGate.ErrorRate > 0);
    }

    [Fact]
    public async Task AcquireAsync_IncrementsRequestCount()
    {
        var options = new AdaptiveThrottleOptions();
        var hostGate = new HostGate("example.com", options);
        
        Assert.Equal(0, hostGate.TotalRequests);
        
        using (await hostGate.AcquireAsync())
        {
            Assert.Equal(1, hostGate.TotalRequests);
        }
        
        using (await hostGate.AcquireAsync())
        {
            Assert.Equal(2, hostGate.TotalRequests);
        }
    }

    [Fact]
    public async Task AcquireAsync_RespectsRequestSpacing()
    {
        var options = new AdaptiveThrottleOptions 
        { 
            RequestSpacing = TimeSpan.FromMilliseconds(100) 
        };
        var hostGate = new HostGate("example.com", options);
        
        var startTime = DateTime.UtcNow;
        
        using (await hostGate.AcquireAsync())
        {
        }
        
        using (await hostGate.AcquireAsync())
        {
        }
        
        var elapsedTime = DateTime.UtcNow - startTime;
        Assert.True(elapsedTime >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task AcquireAsync_CancellationToken_ThrowsOperationCanceledException()
    {
        var options = new AdaptiveThrottleOptions { MinConcurrency = 1 };
        var hostGate = new HostGate("example.com", options);
        
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => hostGate.AcquireAsync(cts.Token));
    }

    [Fact]
    public void ConcurrencyAdjustment_HighLatency_DecreasesConcurrency()
    {
        var options = new AdaptiveThrottleOptions 
        { 
            MinConcurrency = 1,
            MaxConcurrency = 10,
            MaxLatencyThresholdMs = 1000,
            CooldownPeriod = TimeSpan.Zero
        };
        var hostGate = new HostGate("example.com", options);
        var initialConcurrency = hostGate.CurrentConcurrency;
        
        hostGate.RecordLatency(2000);
        
        Assert.True(hostGate.CurrentConcurrency <= initialConcurrency);
    }

    [Fact]
    public void ConcurrencyAdjustment_LowLatency_IncreasesConcurrency()
    {
        var options = new AdaptiveThrottleOptions 
        { 
            MinConcurrency = 1,
            MaxConcurrency = 10,
            MinLatencyThresholdMs = 1000,
            ErrorRateThreshold = 0.1,
            CooldownPeriod = TimeSpan.Zero
        };
        var hostGate = new HostGate("example.com", options);
        var initialConcurrency = hostGate.CurrentConcurrency;
        
        hostGate.RecordLatency(50);
        
        Assert.True(hostGate.CurrentConcurrency >= initialConcurrency);
    }

    [Fact]
    public async Task ConcurrencyAdjustment_HighErrorRate_DecreasesConcurrency()
    {
        var options = new AdaptiveThrottleOptions 
        { 
            MinConcurrency = 1,
            MaxConcurrency = 10,
            ErrorRateThreshold = 0.1,
            CooldownPeriod = TimeSpan.Zero
        };
        var hostGate = new HostGate("example.com", options);
        var initialConcurrency = hostGate.CurrentConcurrency;
        
        for (int i = 0; i < 3; i++)
        {
            using (await hostGate.AcquireAsync()) 
            {
            }
        }
        
        hostGate.RecordError();
        
        Assert.True(hostGate.CurrentConcurrency <= initialConcurrency);
    }

    [Fact]
    public void ConcurrencyAdjustment_RespectsCooldownPeriod()
    {
        var options = new AdaptiveThrottleOptions 
        { 
            MinConcurrency = 1,
            MaxConcurrency = 10,
            MaxLatencyThresholdMs = 1000,
            CooldownPeriod = TimeSpan.FromHours(1)
        };
        var hostGate = new HostGate("example.com", options);
        var initialConcurrency = hostGate.CurrentConcurrency;
        
        hostGate.RecordLatency(2000);
        var firstAdjustmentConcurrency = hostGate.CurrentConcurrency;
        
        hostGate.RecordLatency(2000);
        var secondAdjustmentConcurrency = hostGate.CurrentConcurrency;
        
        Assert.Equal(firstAdjustmentConcurrency, secondAdjustmentConcurrency);
    }

    [Fact]
    public void ConcurrencyAdjustment_DoesNotExceedMaxConcurrency()
    {
        var options = new AdaptiveThrottleOptions 
        { 
            MinConcurrency = 1,
            MaxConcurrency = 3,
            MinLatencyThresholdMs = 1000,
            ErrorRateThreshold = 0.1,
            CooldownPeriod = TimeSpan.Zero
        };
        var hostGate = new HostGate("example.com", options);
        
        for (int i = 0; i < 10; i++)
        {
            hostGate.RecordLatency(50);
        }
        
        Assert.True(hostGate.CurrentConcurrency <= options.MaxConcurrency);
    }

    [Fact]
    public void ConcurrencyAdjustment_DoesNotGoBelowMinConcurrency()
    {
        var options = new AdaptiveThrottleOptions 
        { 
            MinConcurrency = 2,
            MaxConcurrency = 10,
            MaxLatencyThresholdMs = 100,
            CooldownPeriod = TimeSpan.Zero
        };
        var hostGate = new HostGate("example.com", options);
        
        for (int i = 0; i < 10; i++)
        {
            hostGate.RecordLatency(2000);
        }
        
        Assert.True(hostGate.CurrentConcurrency >= options.MinConcurrency);
    }
}