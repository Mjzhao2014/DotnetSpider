using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Throttling;

public class BandwidthThrottledStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _maxBytesPerSecond;
    private readonly object _lock = new();
    private DateTime _lastReadTime = DateTime.UtcNow;
    private long _bytesReadInCurrentSecond;
    private DateTime _currentSecondStart = DateTime.UtcNow;

    public BandwidthThrottledStream(Stream innerStream, long maxBytesPerSecond)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _maxBytesPerSecond = maxBytesPerSecond;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var throttledCount = CalculateThrottledReadSize(count);
        var bytesRead = _innerStream.Read(buffer, offset, throttledCount);
        
        if (bytesRead > 0)
        {
            ApplyBandwidthDelay(bytesRead);
        }
        
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var throttledCount = CalculateThrottledReadSize(count);
        var bytesRead = await _innerStream.ReadAsync(buffer, offset, throttledCount, cancellationToken);
        
        if (bytesRead > 0)
        {
            await ApplyBandwidthDelayAsync(bytesRead, cancellationToken);
        }
        
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var throttledCount = CalculateThrottledWriteSize(count);
        _innerStream.Write(buffer, offset, throttledCount);
        
        if (throttledCount > 0)
        {
            ApplyBandwidthDelay(throttledCount);
        }
        
        if (throttledCount < count)
        {
            Write(buffer, offset + throttledCount, count - throttledCount);
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var throttledCount = CalculateThrottledWriteSize(count);
        await _innerStream.WriteAsync(buffer, offset, throttledCount, cancellationToken);
        
        if (throttledCount > 0)
        {
            await ApplyBandwidthDelayAsync(throttledCount, cancellationToken);
        }
        
        if (throttledCount < count)
        {
            await WriteAsync(buffer, offset + throttledCount, count - throttledCount, cancellationToken);
        }
    }

    private int CalculateThrottledReadSize(int requestedCount)
    {
        return CalculateThrottledSize(requestedCount);
    }

    private int CalculateThrottledWriteSize(int requestedCount)
    {
        return CalculateThrottledSize(requestedCount);
    }

    private int CalculateThrottledSize(int requestedCount)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            
            if (now - _currentSecondStart >= TimeSpan.FromSeconds(1))
            {
                _currentSecondStart = now;
                _bytesReadInCurrentSecond = 0;
            }
            
            var availableBytes = _maxBytesPerSecond - _bytesReadInCurrentSecond;
            if (availableBytes <= 0)
            {
                return 0;
            }
            
            return (int)Math.Min(requestedCount, availableBytes);
        }
    }

    private void ApplyBandwidthDelay(int bytesTransferred)
    {
        lock (_lock)
        {
            _bytesReadInCurrentSecond += bytesTransferred;
            _lastReadTime = DateTime.UtcNow;
            
            if (_bytesReadInCurrentSecond >= _maxBytesPerSecond)
            {
                var timeUntilNextSecond = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _currentSecondStart);
                if (timeUntilNextSecond > TimeSpan.Zero)
                {
                    Thread.Sleep(timeUntilNextSecond);
                }
            }
        }
    }

    private async Task ApplyBandwidthDelayAsync(int bytesTransferred, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _bytesReadInCurrentSecond += bytesTransferred;
            _lastReadTime = DateTime.UtcNow;
        }
        
        if (_bytesReadInCurrentSecond >= _maxBytesPerSecond)
        {
            var timeUntilNextSecond = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _currentSecondStart);
            if (timeUntilNextSecond > TimeSpan.Zero)
            {
                await Task.Delay(timeUntilNextSecond, cancellationToken);
            }
        }
    }

    public override void Flush() => _innerStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_innerStream != null)
        {
            await _innerStream.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}