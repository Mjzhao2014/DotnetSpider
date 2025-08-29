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
/// An <see cref="HttpClientDownloader"/> that incorporates adaptive throttling per-host to dynamically
/// adjust concurrency and pacing based on EWMA latency and recent errors. Also adds retry/backoff
/// handling for transient failures.
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

    public HostGateStats GetHostStats(string host)
    {
        return _throttleManager.GetHostStats(host);
    }

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
        var gate = _throttleManager.GetHostGate(host);
        int attempt = 0;
        Exception lastException = null;
        while (true)
        {
            attempt++;
            HttpResponseMessage httpResponseMessage = null;
            HttpRequestMessage httpRequestMessage = null;
            var stopwatch = new Stopwatch();
            try
            {
                using (await gate.AcquireSlotAsync())
                {
                    httpRequestMessage = request.ToHttpRequestMessage();
                    var httpClient = await CreateClientAsync(request);
                    stopwatch.Start();
                    httpResponseMessage = await SendAsync(httpClient, httpRequestMessage);
                    stopwatch.Stop();
                }
                var elapsed = stopwatch.ElapsedMilliseconds;
                if (httpResponseMessage == null)
                {
                    gate.MarkResult(elapsed, true);
                    throw new SpiderException("No response");
                }
                if (!httpResponseMessage.IsSuccessStatusCode)
                {
                    // check if we should retry
                    var status = httpResponseMessage.StatusCode;
                    bool transient = IsTransientStatusCode(status);
                    gate.MarkResult(elapsed, true);
                    if (transient && attempt <= _options.MaxRetryAttempts)
                    {
                        var delay = GetRetryDelay(httpResponseMessage, attempt);
                        await Task.Delay(delay);
                        continue;
                    }
                    // build failure response
                    var failure = await httpResponseMessage.ToResponseAsync();
                    failure.ElapsedMilliseconds = (int)elapsed;
                    failure.RequestHash = request.Hash;
                    failure.Version = httpResponseMessage.Version;
                    return failure;
                }
                // success path
                var response = await HandleAsync(request, httpResponseMessage);
                if (response == null)
                {
                    response = await httpResponseMessage.ToResponseAsync();
                }
                response.ElapsedMilliseconds = (int)elapsed;
                response.RequestHash = request.Hash;
                response.Version = httpResponseMessage.Version;
                gate.MarkResult(elapsed, false);
                return response;
            }
            catch (Exception e)
            {
                lastException = e;
                gate.MarkResult(0, true);
                if (attempt <= _options.MaxRetryAttempts)
                {
                    var delay = GetRetryDelay(null, attempt);
                    await Task.Delay(delay);
                    continue;
                }
                break;
            }
            finally
            {
                ObjectUtilities.DisposeSafely(Logger, httpResponseMessage, httpRequestMessage);
            }
        }
        return new Response
        {
            RequestHash = request.Hash,
            StatusCode = HttpStatusCode.Gone,
            ReasonPhrase = lastException?.ToString(),
            Version = HttpVersion.Version11
        };
    }

    private bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        // 408 Request Timeout, 429 Too Many Requests, 5xx server errors
        if (statusCode == HttpStatusCode.RequestTimeout ||
            statusCode == (HttpStatusCode)429)
        {
            return true;
        }
        var codeInt = (int)statusCode;
        if (codeInt >= 500 && codeInt <= 599)
        {
            return true;
        }
        return false;
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response != null && response.Headers.TryGetValues("Retry-After", out var values))
        {
            var retryAfterValue = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(retryAfterValue))
            {
                // Retry-After can be seconds or HTTP-date
                if (int.TryParse(retryAfterValue, out var seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
                if (DateTimeOffset.TryParse(retryAfterValue, out var date))
                {
                    var delta = date - DateTimeOffset.UtcNow;
                    if (delta > TimeSpan.Zero)
                    {
                        return delta;
                    }
                }
            }
        }
        // fallback to exponential backoff with jitter
        var backoffSeconds = Math.Pow(2, attempt);
        var jitter = Random.Shared.NextDouble() * 0.1 * backoffSeconds;
        var delay = TimeSpan.FromSeconds(backoffSeconds + jitter);
        if (delay > _options.MaxRetryDelay)
        {
            delay = _options.MaxRetryDelay;
        }
        return delay;
    }
}
