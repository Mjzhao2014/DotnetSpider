using DotnetSpider;
using Xunit;

namespace DotnetSpider.Tests;

/// <summary>
/// Unit tests exercising the per-host adaptive throttling logic.
/// </summary>
public class AdaptiveThrottleTests
{
    [Fact]
    public void Concurrency_Increases_On_Low_Latency_Successes()
    {
        var options = new AdaptiveThrottleOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 5,
            EwmaAlpha = 0.5,
            LatencyLowThreshold = 100,
            LatencyHighThreshold = 1000,
            ErrorRateThreshold = 0.5,
            CooldownMilliseconds = 0
        };
        var gate = new HostGate(options);
        // Simulate a few fast successful requests.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(gate.TryAcquire());
            gate.Release(true, 50);
        }
        Assert.True(gate.ConcurrencyLimit > options.MinConcurrency,
            "Concurrency limit should increase on low latency successes");
    }

    [Fact]
    public void Concurrency_Decreases_On_Errors()
    {
        var options = new AdaptiveThrottleOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 5,
            EwmaAlpha = 0.5,
            LatencyLowThreshold = 100,
            LatencyHighThreshold = 500,
            ErrorRateThreshold = 0.2,
            CooldownMilliseconds = 0
        };
        var gate = new HostGate(options);
        // Increase concurrency first.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(gate.TryAcquire());
            gate.Release(true, 50);
        }
        var increased = gate.ConcurrencyLimit;
        // Now simulate an error with high latency.
        gate.Release(false, 2000);
        Assert.True(gate.ConcurrencyLimit < increased,
            "Concurrency limit should drop on error");
        Assert.True(gate.ConcurrencyLimit >= options.MinConcurrency);
    }

    [Fact]
    public void Hosts_Are_Isolated()
    {
        var options = new AdaptiveThrottleOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 5,
            EwmaAlpha = 0.5,
            LatencyLowThreshold = 100,
            LatencyHighThreshold = 500,
            CooldownMilliseconds = 0
        };
        var gateA = new HostGate(options);
        var gateB = new HostGate(options);
        // Increase concurrency on gateA above the minimum.
        for (var i = 0; i < 3; i++)
        {
            gateA.TryAcquire();
            gateA.Release(true, 50);
        }
        var increased = gateA.ConcurrencyLimit;
        // Now cause gateA to experience an error.
        gateA.TryAcquire();
        gateA.Release(false, 2000);
        var limitAfterError = gateA.ConcurrencyLimit;
        Assert.True(limitAfterError < increased);
        // gateB should remain at minimum concurrency and be unaffected by gateA adjustments.
        Assert.Equal(options.MinConcurrency, gateB.ConcurrencyLimit);
    }
}
