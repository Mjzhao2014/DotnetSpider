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
/// A downloader that wraps <see cref="HttpClientDownloader"/> to provide adaptive per-host
/// concurrency and retry/backoff behaviors using an adaptive throttle manager.
/// </summary>
public class AdaptiveHttpClientDownloader : HttpClientDownloader
{
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly AdaptiveThrottleOptions _options;

    public AdaptiveHttpClientDownloader(
        IHttpClientFactory httpClientFactory,
        IProxyService proxyService,
        ILogger<HttpClientDownloader> logger,
        AdaptiveThrottleManager throttleManager,
        AdaptiveThrottleOptions options)
        : base(httpClientFactory, proxyService, logger)
    {
        _throttleManager = throttleManager;
        _options = options;
    }

    public HostGateStats GetHostStats(string host)
    {
        return _throttleManager.GetHostStats(host);
    }

    public HostGateStats[] GetAllHostStats()
    {
        return _throttleManager.GetAllHostStats();
    }

    /// <summary>
    /// Adaptive implementation of DownloadAsync that applies per-host concurrency limits and retry semantics.
    /// </summary>
    public new async Task<Response> DownloadAsync(Request request)
    {
        if (!_options.EnableAdaptiveThrottling)
        {
            return await base.DownloadAsync(request);
        }

        var host = request.RequestUri.Host;
        var gate = _throttleManager.GetHostGate(host);
        int attempt = 0;
        Response lastResponse = null;

        while (true)
        {
            attempt++;
            IDisposable lease = null;
            var stopwatch = new Stopwatch();
            bool success = false;
            Response response = null;
            try
            {
                lease = await gate.AcquireAsync();
                stopwatch.Start();
                response = await base.DownloadAsync(request);
                stopwatch.Stop();
                success = response.IsSuccessStatusCode;
                if (success)
                {
                    return response;
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "{RequestUri} download failed", request.RequestUri);
                // use sentinel response to track error
                response = new Response
                {
                    RequestHash = request.Hash,
                    StatusCode = HttpStatusCode.Gone,
                    ReasonPhrase = e.ToString(),
                    Version = HttpVersion.Version11
                };
            }
            finally
            {
                stopwatch.Stop();
                var latency = (int)stopwatch.ElapsedMilliseconds;
                gate.UpdateMetrics(latency, success);
                lease?.Dispose();
            }

            // if we've reached here it means we did not return success
            lastResponse = response;
            if (attempt > _options.MaxRetryAttempts || !IsTransientFailure(response))
            {
                return response;
            }
            var delay = CalculateRetryDelay(response, attempt);
            await Task.Delay(delay);
        }
    }

    private static bool IsTransientFailure(Response response)
    {
        if (response == null)
        {
            return true;
        }

        var status = response.StatusCode;
        // 429 or 5xx or request timeout considered transient
        if (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout)
        {
            return true;
        }

        var code = (int)status;
        if (code >= 500 && code <= 599)
        {
            return true;
        }

        return false;
    }

    private TimeSpan CalculateRetryDelay(Response response, int attempt)
    {
        // honor Retry-After header if present
        if (response?.Headers?.RetryAfter != null)
        {
            var retryAfter = response.Headers.RetryAfter.ToString();
            if (int.TryParse(retryAfter, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
            if (DateTimeOffset.TryParse(retryAfter, out var absolute))
            {
                var diff = absolute - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    return diff;
                }
            }
        }

        // exponential backoff with jitter, capped by MaxRetryDelay
        var backoffMs = Math.Pow(2, attempt) * 1000; // start at 1s and double
        var jitter = Random.Shared.NextDouble() * 0.5 + 0.5; // 0.5-1.0
        var delay = TimeSpan.FromMilliseconds(backoffMs * jitter);
        if (delay > _options.MaxRetryDelay)
        {
            delay = _options.MaxRetryDelay;
        }
        return delay;
    }
}
