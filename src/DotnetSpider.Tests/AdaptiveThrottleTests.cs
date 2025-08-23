using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Downloader;
using Xunit;

namespace DotnetSpider.Tests;

/// <summary>
/// Tests around the adaptive concurrency and per-host gating used by adaptive throttling.
/// </summary>
public class AdaptiveThrottleTests
{
    [Fact]
    public async Task Concurrency_Increases_With_Low_Latency()
    {
        var opts = new AdaptiveThrottleOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 4,
            MinLatencyThresholdMs = 100,
            MaxLatencyThresholdMs = 5000,
            ErrorRateThreshold = 0.1,
            EwmaAlpha = 0.5,
            CooldownPeriod = TimeSpan.Zero // allow adjustment every call
        };
        var gate = new HostGate(opts);
        Assert.Equal(1, gate.ConcurrencyLimit);
        // feed a series of fast successes
        for (int i = 0; i < 5; ++i)
        {
            using (await gate.AcquireAsync())
            {
                gate.RecordResult(50, false);
            }
        }
        // concurrency should have increased from baseline
        Assert.True(gate.ConcurrencyLimit > 1);
    }

    [Fact]
    public async Task Concurrency_Decreases_On_Errors()
    {
        var opts = new AdaptiveThrottleOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 5,
            MinLatencyThresholdMs = 50,
            MaxLatencyThresholdMs = 200,
            ErrorRateThreshold = 0.1,
            EwmaAlpha = 0.5,
            CooldownPeriod = TimeSpan.Zero
        };
        var gate = new HostGate(opts);
        // ramp up concurrency slightly first
        for (int i = 0; i < 3; ++i)
        {
            using (await gate.AcquireAsync())
            {
                gate.RecordResult(10, false);
            }
        }
        var before = gate.ConcurrencyLimit;
        // now feed errors/high latency to trigger demotion
        for (int i = 0; i < 3; ++i)
        {
            using (await gate.AcquireAsync())
            {
                gate.RecordResult(5000, true);
            }
        }
        Assert.True(gate.ConcurrencyLimit < before);
    }

    [Fact]
    public async Task Hosts_Isolate_Their_Throttling()
    {
        var opts = new AdaptiveThrottleOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 5,
            MinLatencyThresholdMs = 50,
            MaxLatencyThresholdMs = 200,
            ErrorRateThreshold = 0.1,
            EwmaAlpha = 0.5,
            CooldownPeriod = TimeSpan.Zero
        };
        var gate1 = new HostGate(opts);
        var gate2 = new HostGate(opts);
        // ramp up gate1
        for (int i = 0; i < 3; ++i)
        {
            using (await gate1.AcquireAsync())
            {
                gate1.RecordResult(10, false);
            }
        }
        // force errors on gate2
        for (int i = 0; i < 3; ++i)
        {
            using (await gate2.AcquireAsync())
            {
                gate2.RecordResult(1000, true);
            }
        }
        Assert.True(gate1.ConcurrencyLimit > gate2.ConcurrencyLimit);
    }

    [Fact]
    public async Task Acquire_Waits_When_Saturated()
    {
        var opts = new AdaptiveThrottleOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 1,
            CooldownPeriod = TimeSpan.Zero
        };
        var gate = new HostGate(opts);
        var sw = Stopwatch.StartNew();
        // take the only slot
        var handle = await gate.AcquireAsync();
        var acquireTask = gate.AcquireAsync();
        // after short delay, release first handle so second can proceed
        await Task.Delay(100);
        handle.Dispose();
        await acquireTask;
        sw.Stop();
        // second acquire waited ~100ms while first held the only concurrency slot
        Assert.True(sw.ElapsedMilliseconds >= 100);
    }

    [Fact]
    public async Task Request_Spacing_Respected_For_Serial_Requests()
    {
        var opts = new AdaptiveThrottleOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 1,
            RequestSpacing = TimeSpan.FromMilliseconds(200),
            CooldownPeriod = TimeSpan.Zero
        };
        var gate = new HostGate(opts);
        var sw = Stopwatch.StartNew();
        using (await gate.AcquireAsync())
        {
            gate.RecordResult(10, false);
        }
        using (await gate.AcquireAsync())
        {
            gate.RecordResult(10, false);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 200);
    }
}
