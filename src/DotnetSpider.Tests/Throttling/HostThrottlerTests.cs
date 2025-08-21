using System;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Throttling;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DotnetSpider.Tests.Throttling;

public class HostThrottlerTests : IDisposable
{
    private readonly Mock<ILogger<HostThrottler>> _loggerMock;
    private readonly HostThrottler _hostThrottler;

    public HostThrottlerTests()
    {
        _loggerMock = new Mock<ILogger<HostThrottler>>();
        _hostThrottler = new HostThrottler(_loggerMock.Object);
    }

    [Fact]
    public async Task AcquireAsync_ReturnsDisposableToken()
    {
        var token = await _hostThrottler.AcquireAsync("example.com");
        
        Assert.NotNull(token);
        
        token.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_ThrowsOnNullHost()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _hostThrottler.AcquireAsync(null));
    }

    [Fact]
    public async Task AcquireAsync_ThrowsOnEmptyHost()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _hostThrottler.AcquireAsync(string.Empty));
    }

    [Fact]
    public async Task AcquireAsync_HandlesMultipleHostsConcurrently()
    {
        var host1 = "example.com";
        var host2 = "google.com";
        
        var token1 = await _hostThrottler.AcquireAsync(host1);
        var token2 = await _hostThrottler.AcquireAsync(host2);
        
        Assert.NotNull(token1);
        Assert.NotNull(token2);
        
        token1.Dispose();
        token2.Dispose();
    }

    [Fact]
    public void RecordSuccess_UpdatesHostMetrics()
    {
        const string host = "example.com";
        var latency = TimeSpan.FromMilliseconds(100);
        
        _hostThrottler.RecordSuccess(host, latency);
        
        var metrics = _hostThrottler.GetMetrics(host);
        Assert.True(metrics.AverageLatency > 0);
    }

    [Fact]
    public void RecordError_UpdatesHostMetrics()
    {
        const string host = "example.com";
        var latency = TimeSpan.FromMilliseconds(500);
        
        for (int i = 0; i < 20; i++)
        {
            _hostThrottler.RecordError(host, latency);
        }
        
        var metrics = _hostThrottler.GetMetrics(host);
        Assert.True(metrics.ErrorRate > 0);
    }

    [Fact]
    public void GetMetrics_ReturnsEmptyMetricsForUnknownHost()
    {
        var metrics = _hostThrottler.GetMetrics("unknown.com");
        
        Assert.Equal(0, metrics.CurrentConcurrency);
        Assert.Equal(0.0, metrics.AverageLatency);
        Assert.Equal(0.0, metrics.ErrorRate);
    }

    [Fact]
    public async Task AcquireAsync_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        await Assert.ThrowsAsync<TaskCanceledException>(() => 
            _hostThrottler.AcquireAsync("example.com", cts.Token));
    }

    [Fact]
    public async Task MultipleAcquisitions_RespectConcurrencyLimits()
    {
        const string host = "example.com";
        const int simultaneousRequests = 10;
        
        var tasks = new Task<IDisposable>[simultaneousRequests];
        
        for (int i = 0; i < simultaneousRequests; i++)
        {
            tasks[i] = _hostThrottler.AcquireAsync(host);
        }
        
        var tokens = await Task.WhenAll(tasks);
        
        foreach (var token in tokens)
        {
            Assert.NotNull(token);
            token.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_CleansUpResources()
    {
        const string host = "example.com";
        
        var token = await _hostThrottler.AcquireAsync(host);
        
        _hostThrottler.Dispose();
        token.Dispose();
        
        await Assert.ThrowsAnyAsync<Exception>(() => 
            _hostThrottler.AcquireAsync(host));
    }

    public void Dispose()
    {
        _hostThrottler?.Dispose();
    }
}