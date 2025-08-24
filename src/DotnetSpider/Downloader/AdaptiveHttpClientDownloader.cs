using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.Proxy;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Downloader;

/// <summary>
/// An HttpClient-based downloader that coordinates requests per-host
/// using an adaptive throttle manager. For each host a HostGate governs
/// concurrency, pacing between requests, and tracks rolling EWMA latency
/// and error rate to adjust concurrency up or down within configured bounds.
/// Transient failures are automatically retried honoring Retry-After headers
/// and jittered exponential backoff.
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
    /// Exposes current stats for a given host for diagnostics.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        return _throttleManager.GetHostStats(host);
    }

    /// <summary>
    /// Exposes all current host gate stats.
    /// </summary>
    public HostGateStats[] GetAllHostStats()
    {
        return _throttleManager.GetAllHostStats();
    }

    public override async Task<Response> DownloadAsync(Request request)
    {
        if (!_options.EnableAdaptiveThrottling)
        {
            return await base.DownloadAsync(request);
        }

        var host = request.RequestUri.Host;
        var gate = _throttleManager.GetOrCreateGate(host);
        var attempt = 0;
        while (true)
        {
            // Wait for an execution slot for this host
            using (await gate.AcquireAsync())
            {
                HttpResponseMessage httpResponseMessage = null;
                HttpRequestMessage httpRequestMessage = null;
                var sw = new Stopwatch();
                try
                {
                    httpRequestMessage = request.ToHttpRequestMessage();
                    var httpClient = await CreateClientAsync(request);
                    sw.Start();
                    httpResponseMessage = await SendAsync(httpClient, httpRequestMessage);
                    sw.Stop();
                    // Update throttle info based on success/error
                    // treat any status code < 400 as success for throttling purposes
                    var success = ((int)httpResponseMessage.StatusCode) < 400;
                    gate.MarkResult(TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds), success);
                    if (!success && ShouldRetry(httpResponseMessage.StatusCode) && attempt < _options.MaxRetryAttempts)
                    {
                        attempt++;
                        var delay = GetRetryDelay(httpResponseMessage, attempt);
                        await Task.Delay(delay);
                        continue;
                    }
                    var response = await HandleAsync(request, httpResponseMessage);
                    if (response != null)
                    {
                        response.Version = response.Version ?? HttpVersion.Version11;
                        return response;
                    }
                    response = await httpResponseMessage.ToResponseAsync();
                    response.ElapsedMilliseconds = (int)sw.ElapsedMilliseconds;
                    response.RequestHash = request.Hash;
                    response.Version = httpResponseMessage.Version;
                    return response;
                }
                catch (Exception e)
                {
                    sw.Stop();
                    Logger.LogError(e, "{RequestUri} download failed", request.RequestUri);
                    gate.MarkResult(TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds), success: false);
                    if (attempt < _options.MaxRetryAttempts)
                    {
                        attempt++;
                        var delay = GetRetryDelay(null, attempt);
                        await Task.Delay(delay);
                        continue;
                    }
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
                    ObjectUtilities.DisposeSafely(Logger, httpResponseMessage, httpRequestMessage);
                }
            }
        }
    }

    private bool ShouldRetry(HttpStatusCode code)
    {
        var numeric = (int)code;
        if (numeric == 408 || numeric == 429) return true;
        if (numeric >= 500 && numeric < 600) return true;
        return false;
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response != null && response.Headers?.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta != null)
            {
                return response.Headers.RetryAfter.Delta.Value;
            }
            if (response.Headers.RetryAfter.Date != null)
            {
                var delta = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero) return delta;
            }
        }
        // exponential backoff with jitter
        var baseDelayMs = Math.Pow(2, attempt) * 100; // start at 100ms
        var jitterFactor = 0.5 + Random.Shared.NextDouble();
        var delay = TimeSpan.FromMilliseconds(Math.Min(_options.MaxRetryDelay.TotalMilliseconds, baseDelayMs * jitterFactor));
        return delay;
    }
}
