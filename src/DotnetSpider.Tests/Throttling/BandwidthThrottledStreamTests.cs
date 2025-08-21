using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DotnetSpider.Throttling;
using Xunit;

namespace DotnetSpider.Tests.Throttling;

public class BandwidthThrottledStreamTests : IDisposable
{
    private readonly MemoryStream _innerStream;
    private readonly byte[] _testData;
    
    public BandwidthThrottledStreamTests()
    {
        _testData = new byte[10000];
        Random.Shared.NextBytes(_testData);
        _innerStream = new MemoryStream(_testData);
    }

    [Fact]
    public void Constructor_ThrowsOnNullStream()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new BandwidthThrottledStream(null, 1000));
    }

    [Fact]
    public void Properties_ReflectInnerStream()
    {
        using var throttledStream = new BandwidthThrottledStream(_innerStream, 1000);
        
        Assert.Equal(_innerStream.CanRead, throttledStream.CanRead);
        Assert.Equal(_innerStream.CanSeek, throttledStream.CanSeek);
        Assert.Equal(_innerStream.CanWrite, throttledStream.CanWrite);
        Assert.Equal(_innerStream.Length, throttledStream.Length);
        Assert.Equal(_innerStream.Position, throttledStream.Position);
    }

    [Fact]
    public void Read_ReturnsDataFromInnerStream()
    {
        using var throttledStream = new BandwidthThrottledStream(_innerStream, 100000);
        var buffer = new byte[100];
        
        var bytesRead = throttledStream.Read(buffer, 0, buffer.Length);
        
        Assert.Equal(100, bytesRead);
        Assert.Equal(_testData[0..100], buffer);
    }

    [Fact]
    public async Task ReadAsync_ReturnsDataFromInnerStream()
    {
        using var throttledStream = new BandwidthThrottledStream(_innerStream, 100000);
        var buffer = new byte[100];
        
        var bytesRead = await throttledStream.ReadAsync(buffer, 0, buffer.Length);
        
        Assert.Equal(100, bytesRead);
        Assert.Equal(_testData[0..100], buffer);
    }

    [Fact]
    public void Read_ThrottlesBandwidth()
    {
        const int maxBytesPerSecond = 1000;
        const int readSize = 500;
        
        using var throttledStream = new BandwidthThrottledStream(_innerStream, maxBytesPerSecond);
        var buffer = new byte[readSize];
        
        var stopwatch = Stopwatch.StartNew();
        
        throttledStream.Read(buffer, 0, readSize);
        throttledStream.Read(buffer, 0, readSize);
        throttledStream.Read(buffer, 0, readSize);
        
        stopwatch.Stop();
        
        Assert.True(stopwatch.ElapsedMilliseconds > 500);
    }

    [Fact]
    public async Task ReadAsync_ThrottlesBandwidth()
    {
        const int maxBytesPerSecond = 1000;
        const int readSize = 500;
        
        using var throttledStream = new BandwidthThrottledStream(_innerStream, maxBytesPerSecond);
        var buffer = new byte[readSize];
        
        var stopwatch = Stopwatch.StartNew();
        
        await throttledStream.ReadAsync(buffer, 0, readSize);
        await throttledStream.ReadAsync(buffer, 0, readSize);
        await throttledStream.ReadAsync(buffer, 0, readSize);
        
        stopwatch.Stop();
        
        Assert.True(stopwatch.ElapsedMilliseconds > 500);
    }

    [Fact]
    public void Write_ThrottlesBandwidth()
    {
        const int maxBytesPerSecond = 1000;
        const int writeSize = 500;
        
        using var writeStream = new MemoryStream();
        using var throttledStream = new BandwidthThrottledStream(writeStream, maxBytesPerSecond);
        var data = new byte[writeSize];
        
        var stopwatch = Stopwatch.StartNew();
        
        throttledStream.Write(data, 0, writeSize);
        throttledStream.Write(data, 0, writeSize);
        throttledStream.Write(data, 0, writeSize);
        
        stopwatch.Stop();
        
        Assert.True(stopwatch.ElapsedMilliseconds > 500);
        Assert.Equal(writeSize * 3, writeStream.Length);
    }

    [Fact]
    public async Task WriteAsync_ThrottlesBandwidth()
    {
        const int maxBytesPerSecond = 1000;
        const int writeSize = 500;
        
        using var writeStream = new MemoryStream();
        using var throttledStream = new BandwidthThrottledStream(writeStream, maxBytesPerSecond);
        var data = new byte[writeSize];
        
        var stopwatch = Stopwatch.StartNew();
        
        await throttledStream.WriteAsync(data, 0, writeSize);
        await throttledStream.WriteAsync(data, 0, writeSize);
        await throttledStream.WriteAsync(data, 0, writeSize);
        
        stopwatch.Stop();
        
        Assert.True(stopwatch.ElapsedMilliseconds > 500);
        Assert.Equal(writeSize * 3, writeStream.Length);
    }

    [Fact]
    public void Seek_DelegatesToInnerStream()
    {
        using var throttledStream = new BandwidthThrottledStream(_innerStream, 1000);
        
        var position = throttledStream.Seek(100, SeekOrigin.Begin);
        
        Assert.Equal(100, position);
        Assert.Equal(100, throttledStream.Position);
    }

    [Fact]
    public void SetLength_DelegatesToInnerStream()
    {
        using var memStream = new MemoryStream();
        using var throttledStream = new BandwidthThrottledStream(memStream, 1000);
        
        throttledStream.SetLength(500);
        
        Assert.Equal(500, throttledStream.Length);
    }

    [Fact]
    public void Flush_DelegatesToInnerStream()
    {
        using var throttledStream = new BandwidthThrottledStream(_innerStream, 1000);
        
        throttledStream.Flush();
    }

    [Fact]
    public async Task FlushAsync_DelegatesToInnerStream()
    {
        using var throttledStream = new BandwidthThrottledStream(_innerStream, 1000);
        
        await throttledStream.FlushAsync();
    }

    public void Dispose()
    {
        _innerStream?.Dispose();
    }
}