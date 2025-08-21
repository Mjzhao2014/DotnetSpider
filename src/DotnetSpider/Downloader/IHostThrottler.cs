using System.Threading.Tasks;

namespace DotnetSpider.Downloader;

/// <summary>
/// Central manager for per-host throttle state. Responsible for coordinating access to per-host
/// state such as dynamic concurrency and error rates, and exposing a simple acquire/release
/// API used by the download pipeline to pace requests.
/// </summary>
public interface IHostThrottler
{
    /// <summary>
    /// Acquire a slot for the given host so a request can be issued. This will asynchronously
    /// block if the current number of in-flight requests exceeds the dynamic concurrency limit
    /// for that host.
    /// </summary>
    Task AcquireAsync(string host);

    /// <summary>
    /// Release a previously acquired slot for the host and incorporate the outcome of the request.
    /// </summary>
    void Release(string host, bool success, int elapsedMs, int statusCode);
}
