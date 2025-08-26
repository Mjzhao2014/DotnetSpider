using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.Proxy;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Downloader;

/// <summary>
/// An HttpClient-based downloader that participates in per-host adaptive throttling.
/// Each host has its own concurrency gate governed by latency and error rate signals.
/// Transient failures (timeouts, 429, 5xx) will be retried with exponential backoff or server-provided Retry-After.
/// </summary>
public class AdaptiveHttpClientDownloader : HttpClientDownloader, IDownloader
{
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly AdaptiveThrottleOptions _options;

    private static readonly Random Jitter = new();

    public AdaptiveHttpClientDownloader(
        IHttpClientFactory httpClientFactory,
        IProxyService proxyService,
        ILogger<AdaptiveHttpClientDownloader> logger,
        AdaptiveThrottleManager throttleManager,
        AdaptiveThrottleOptions options) : base(httpClientFactory, proxyService, logger)
    {
        _throttleManager = throttleManager;
        _options = options;
    }

    /// <summary>
    /// Expose stats for the currently known host, or null if the host has not been seen yet.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        return _throttleManager.GetHostStats(host);
    }

    /// <summary>
    /// Return an array of stats for all hosts currently being tracked.
    /// </summary>
    public HostGateStats[] GetAllHostStats()
    {
        return _throttleManager.GetAllHostStats();
    }

    /// <summary>
    /// Entry point for download requests invoked through IDownloader.
    /// This hides the base class implementation in order to inject adaptive
    /// throttling and retry logic.
    /// </summary>
    public new async Task<Response> DownloadAsync(Request request)
    {
        // If adaptive throttling is disabled, just delegate to base implementation.
        if (!_options.EnableAdaptiveThrottling)
        {
            return await base.DownloadAsync(request);
        }

        var host = request.RequestUri.Host;
        var gate = _throttleManager.GetHostGate(host);
        int attempts = Math.Max(1, _options.MaxRetryAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            IDisposable slot = null;
            HttpResponseMessage httpResponseMessage = null;
            HttpRequestMessage httpRequestMessage = null;
            try
            {
                slot = await gate.WaitAsync().ConfigureAwait(false);
                httpRequestMessage = request.ToHttpRequestMessage();
                var httpClient = await CreateClientAsync(request).ConfigureAwait(false);
                var stopwatch = Stopwatch.StartNew();
                httpResponseMessage = await SendAsync(httpClient, httpRequestMessage).ConfigureAwait(false);
                stopwatch.Stop();
                var latency = stopwatch.Elapsed;
                var response = await HandleAsync(request, httpResponseMessage).ConfigureAwait(false);
                if (response != null)
                {
                    response.Version ??= HttpVersion.Version11;
                    gate.RecordResult(latency, true);
                    return response;
                }
                response = await httpResponseMessage.ToResponseAsync().ConfigureAwait(false);
                response.ElapsedMilliseconds = (int)latency.TotalMilliseconds;
                response.RequestHash = request.Hash;
                response.Version = httpResponseMessage.Version;
                var isError = (int)response.StatusCode >= 400;
                gate.RecordResult(latency, isError);
                if (IsTransient(response.StatusCode) && attempt < attempts)
                {
                    await DelayForRetryAsync(httpResponseMessage, attempt).ConfigureAwait(false);
                    continue;
                }
                return response;
            }
            catch (Exception e)
            {
                // treat any thrown exception (including timeouts) as an error for throttling purposes
                gate.RecordResult(TimeSpan.Zero, true);
                if (attempt < attempts)
                {
                    await DelayForRetryAsync(httpResponseMessage, attempt).ConfigureAwait(false);
                    continue;
                }
                Logger.LogError(e, "{RequestUri} download failed", request.RequestUri);
                return new Response
                {
                    RequestHash = request.Hash,
                    StatusCode = HttpStatusCode.Gone,
                    ReasonPhrase = e.ToString(),
                    Version = HttpVersion.Version11
                };
            }
            finally
            {
                slot?.Dispose();
                ObjectUtilities.DisposeSafely(Logger, httpResponseMessage, httpRequestMessage);
            }
        }
        // Should not reach here
        return null;
    }

    /// <summary>
    /// Explicit interface implementation ensures that calls resolved through the IDownloader
    /// interface are dispatched to this derived implementation rather than the base class.
    /// </summary>
    Task<Response> IDownloader.DownloadAsync(Request request)
    {
        return DownloadAsync(request);
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout || code == 429 || code >= 500;
    }

    private async Task DelayForRetryAsync(HttpResponseMessage response, int attempt)
    {
        TimeSpan delay = TimeSpan.Zero;
        if (response != null && response.Headers?.RetryAfter != null)
        {
            var retry = response.Headers.RetryAfter;
            if (retry.Delta.HasValue)
            {
                delay = retry.Delta.Value;
            }
            else if (retry.Date.HasValue)
            {
                var retryAt = retry.Date.Value;
                var delta = retryAt - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                {
                    delay = delta;
                }
            }
        }
        if (delay == TimeSpan.Zero)
        {
            // simple exponential backoff with jitter between retries
            var maxDelay = _options.MaxRetryDelay.TotalMilliseconds;
            var backoff = Math.Min(Math.Pow(2, attempt - 1) * 200.0, maxDelay);
            var jitterFactor = 0.5 + Jitter.NextDouble(); // 0.5 .. 1.5
            delay = TimeSpan.FromMilliseconds(backoff * jitterFactor);
        }
        // ensure delay is not negative
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }
        await Task.Delay(delay).ConfigureAwait(false);
    }
}
