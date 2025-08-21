using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Stream wrapper that throttles throughput to a target average bytes-per-second.
/// Upon each read or write, computes how long the operation *should* have taken based
/// on the target bandwidth and delays if the underlying operation completed too quickly,
/// thereby smoothing out bursty IO to an approximate bandwidth ceiling. A small random jitter
/// can also be optionally applied to avoid synchronization artefacts.
/// </summary>
public class BandwidthThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly int _bytesPerSecond;
    private readonly bool _applyJitter;

    public BandwidthThrottledStream(Stream inner, int bytesPerSecond, bool applyJitter = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        _bytesPerSecond = bytesPerSecond;
        _applyJitter = applyJitter;
    }

    private static TimeSpan ComputeTargetDuration(int bytes, int bytesPerSecond)
    {
        var seconds = (double)bytes / bytesPerSecond;
        return TimeSpan.FromSeconds(seconds);
    }

    private static int ComputeJitterMillis()
    {
        return Random.Shared.Next(0, 50);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var start = DateTime.UtcNow;
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        var elapsed = DateTime.UtcNow - start;
        var target = ComputeTargetDuration(read, _bytesPerSecond);
        var delay = target - elapsed;
        if (delay > TimeSpan.Zero)
        {
            if (_applyJitter)
            {
                delay += TimeSpan.FromMilliseconds(ComputeJitterMillis());
            }
            await Task.Delay(delay, cancellationToken);
        }
        return read;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var start = DateTime.UtcNow;
        await _inner.WriteAsync(buffer, cancellationToken);
        var elapsed = DateTime.UtcNow - start;
        var target = ComputeTargetDuration(buffer.Length, _bytesPerSecond);
        var delay = target - elapsed;
        if (delay > TimeSpan.Zero)
        {
            if (_applyJitter)
            {
                delay += TimeSpan.FromMilliseconds(ComputeJitterMillis());
            }
            await Task.Delay(delay, cancellationToken);
        }
    }

    #region Stream overrides
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    #endregion
}
