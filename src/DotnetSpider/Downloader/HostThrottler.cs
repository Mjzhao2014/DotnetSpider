using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Default implementation of <see cref="IHostThrottler"/> using a simple concurrent dictionary
/// of per-host state objects to coordinate adaptive throttling on a per-host basis.
/// </summary>
public class HostThrottler : IHostThrottler
{
    private readonly ConcurrentDictionary<string, HostThrottleState> _states = new();

    /// <summary>
    /// Acquire a permit for the given host, asynchronously waiting until the host's dynamic
    /// concurrency limit allows another request.
    /// </summary>
    public Task AcquireAsync(string host)
    {
        var state = _states.GetOrAdd(host, _ => new HostThrottleState());
        return state.AcquireAsync();
    }

    /// <summary>
    /// Release a permit previously acquired for this host and update the adaptive state
    /// with the outcome and latency of the request.
    /// </summary>
    public void Release(string host, bool success, int elapsedMs, int statusCode)
    {
        var state = _states.GetOrAdd(host, _ => new HostThrottleState());
        state.Release(success, elapsedMs, statusCode);
    }
}
