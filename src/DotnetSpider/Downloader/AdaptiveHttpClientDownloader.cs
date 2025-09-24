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
/// An HttpClient-based downloader which incorporates adaptive per-host throttling based on
/// observed latency and error responses. Concurrency, pacing and retry delays are dynamically
/// tuned separately for each host encountered.
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
    /// Returns the current stats snapshot for a particular host, if any requests have been issued.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        return _throttleManager.GetHostStats(host);
    }

    /// <summary>
    /// Returns stats snapshots for all host gates currently tracked by this downloader.
    /// </summary>
    public HostGateStats[] GetAllHostStats()
    {
        return _throttleManager.GetAllHostStats();
    }

    /// <summary>
    /// Download the request applying per-host adaptive throttling and built-in retry logic.
    /// </summary>
    public new async Task<Response> DownloadAsync(Request request)
    {
        if (!_options.EnableAdaptiveThrottling)
        {
            return await base.DownloadAsync(request);
        }

        var host = request.RequestUri.Host;
        var hostGate = _throttleManager.GetHostGate(host);
        // initial attempt count is 0
        for (var attempt = 0; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            HttpResponseMessage httpResponseMessage = null;
            HttpRequestMessage httpRequestMessage = null;
            var stopwatch = new Stopwatch();
            try
            {
                await hostGate.AcquireAsync().ConfigureAwait(false);
                httpRequestMessage = request.ToHttpRequestMessage();
                var httpClient = await CreateClientAsync(request).ConfigureAwait(false);
                await hostGate.WaitForRequestWindowAsync().ConfigureAwait(false);
                stopwatch.Start();
                httpResponseMessage = await SendAsync(httpClient, httpRequestMessage).ConfigureAwait(false);
                stopwatch.Stop();
                var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                var responseHandled = await HandleAsync(request, httpResponseMessage).ConfigureAwait(false);
                Response response;
                if (responseHandled != null)
                {
                    response = responseHandled;
                }
                else
                {
                    response = await httpResponseMessage.ToResponseAsync().ConfigureAwait(false);
                }
                response.ElapsedMilliseconds = (int)elapsedMilliseconds;
                response.RequestHash = request.Hash;
                response.Version ??= httpResponseMessage.Version;
                if (IsSuccessStatus(response.StatusCode))
                {
                    hostGate.Release(false, elapsedMilliseconds);
                    return response;
                }

                // treat any non-success as an error for throttling
                hostGate.Release(true, elapsedMilliseconds);
                if (attempt < _options.MaxRetryAttempts && IsRetryable(response.StatusCode))
                {
                    var delay = ComputeRetryDelay(response, attempt);
                    await Task.Delay(delay).ConfigureAwait(false);
                    continue;
                }
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                // exceptions are treated as errors for throttling
                hostGate.Release(true, stopwatch.ElapsedMilliseconds);
                if (attempt >= _options.MaxRetryAttempts)
                {
                    Logger.LogError(ex, "{RequestUri} download failed", request.RequestUri);
                    return new Response
                    {
                        RequestHash = request.Hash,
                        StatusCode = HttpStatusCode.Gone,
                        ReasonPhrase = ex.ToString(),
                        Version = HttpVersion.Version11
                    };
                }
                var delay = ComputeRetryDelay(null, attempt);
                await Task.Delay(delay).ConfigureAwait(false);
                // retry
            }
            finally
            {
                ObjectUtilities.DisposeSafely(Logger, httpResponseMessage, httpRequestMessage);
            }
        }

        // Should not reach here, but as a safeguard use base error response
        return new Response
        {
            RequestHash = request.Hash,
            StatusCode = HttpStatusCode.Gone,
            ReasonPhrase = "Maximum retries exceeded",
            Version = HttpVersion.Version11
        };
    }

    private static bool IsSuccessStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code >= 200 && code <= 299;
    }

    private static bool IsRetryable(HttpStatusCode status)
    {
        if (status == HttpStatusCode.Forbidden || status == HttpStatusCode.NotFound)
        {
            return false;
        }
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

    private TimeSpan ComputeRetryDelay(Response response, int attempt)
    {
        // Check for Retry-After header on response; DotnetSpider exposes strongly-typed header properties.
        if (response != null && response.Headers != null)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (!string.IsNullOrEmpty(retryAfter))
            {
                // Retry-After can be integer seconds or HTTP-date
                if (int.TryParse(retryAfter, out var seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
                if (DateTimeOffset.TryParse(retryAfter, out var dt))
                {
                    var diff = dt - DateTimeOffset.UtcNow;
                    if (diff > TimeSpan.Zero)
                    {
                        return diff;
                    }
                }
            }
        }
        // Exponential backoff with jitter
        var baseDelayMs = 500.0;
        var exponent = Math.Pow(2, attempt);
        var delayMs = baseDelayMs * exponent;
        if (delayMs > _options.MaxRetryDelay.TotalMilliseconds)
        {
            delayMs = _options.MaxRetryDelay.TotalMilliseconds;
        }
        // jitter factor between 0.5 and 1.5
        var jitterFactor = 0.5 + Random.Shared.NextDouble();
        delayMs *= jitterFactor;
        if (delayMs > _options.MaxRetryDelay.TotalMilliseconds)
        {
            delayMs = _options.MaxRetryDelay.TotalMilliseconds;
        }
        return TimeSpan.FromMilliseconds(delayMs);
    }
}
