using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Proxy;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Downloader;

/// <summary>
/// An <see cref="HttpClientDownloader"/> that wraps downloads with per-host adaptive throttling:
/// concurrency gating, pacing of requests, EWMA latency tracking and automatic retry with backoff.
/// </summary>
public class AdaptiveHttpClientDownloader : HttpClientDownloader
{
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly AdaptiveThrottleOptions _options;

    public AdaptiveHttpClientDownloader(
        IHttpClientFactory httpClientFactory,
        IProxyService proxyService,
        ILogger<AdaptiveHttpClientDownloader> logger,
        AdaptiveThrottleManager throttleManager,
        AdaptiveThrottleOptions options)
        : base(httpClientFactory, proxyService, logger)
    {
        _throttleManager = throttleManager;
        _options = options;
    }

    /// <summary>
    /// Statistics snapshot for a given host.
    /// </summary>
    public HostGateStats GetHostStats(string host) => _throttleManager.GetHostStats(host);

    /// <summary>
    /// Statistics snapshot for all current host gates.
    /// </summary>
    public HostGateStats[] GetAllHostStats() => _throttleManager.GetAllHostStats();

    /// <summary>
    /// Override of <see cref="HttpClientDownloader.DownloadAsync"/> that obtains a per-host
    /// gate and enforces concurrency and pacing, and retries transient errors using exponential backoff.
    /// </summary>
    public new async Task<Response> DownloadAsync(Request request)
    {
        if (!_options.EnableAdaptiveThrottling)
        {
            return await base.DownloadAsync(request).ConfigureAwait(false);
        }

        Response lastResponse = null;
        var host = request.RequestUri.Host;
        var gate = _throttleManager.GetHostGate(host);
        await gate.EnterAsync().ConfigureAwait(false);
        try
        {
            var attempt = 0;
            while (true)
            {
                attempt++;
                await gate.WaitForSpacingAsync().ConfigureAwait(false);
                var sw = Stopwatch.StartNew();
                lastResponse = await base.DownloadAsync(request).ConfigureAwait(false);
                sw.Stop();
                var latency = sw.ElapsedMilliseconds;
                var isError = (int)lastResponse.StatusCode >= 400;
                gate.RecordResult(latency, isError);
                if (!isError)
                {
                    return lastResponse;
                }
                if (!IsTransient(lastResponse.StatusCode) || attempt >= _options.MaxRetryAttempts)
                {
                    return lastResponse;
                }

                // compute retry delay:
                var delay = ComputeRetryDelay(lastResponse, attempt);
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Leave();
        }
    }

    private static readonly Random _jitter = Random.Shared;

    private static bool IsTransient(HttpStatusCode status)
    {
        var code = (int)status;
        return code == 408 || code == 429 || code >= 500;
    }

    private TimeSpan ComputeRetryDelay(Response response, int attempt)
    {
        // honor Retry-After header if available
        if (!string.IsNullOrWhiteSpace(response.Headers.RetryAfter))
        {
            if (int.TryParse(response.Headers.RetryAfter.ToString(), out var secs))
            {
                return TimeSpan.FromSeconds(secs);
            }
            if (DateTimeOffset.TryParse(response.Headers.RetryAfter.ToString(), out var date))
            {
                var delta = date - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                {
                    return delta;
                }
            }
        }

        // exponential backoff with jitter
        var backoffSeconds = Math.Pow(2, attempt - 1);
        var jitterSeconds = _jitter.NextDouble();
        var delay = TimeSpan.FromSeconds(backoffSeconds + jitterSeconds);
        if (delay > _options.MaxRetryDelay)
        {
            delay = _options.MaxRetryDelay;
        }
        return delay;
    }
}
