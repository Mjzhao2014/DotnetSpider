using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Proxy;
using Microsoft.Extensions.Logging;
using DotnetSpider.Infrastructure;

namespace DotnetSpider.Downloader;

/// <summary>
/// HttpClientDownloader with per-host adaptive throttling and retry handling.
/// </summary>
public class AdaptiveHttpClientDownloader : HttpClientDownloader
{
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly AdaptiveThrottleOptions _options;
    private readonly Random _rng = new();

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

    /// <summary>
    /// Download a request, respecting per-host adaptive throttling and retrying transient failures.
    /// </summary>
    public new async Task<Response> DownloadAsync(Request request)
    {
        if (!_options.EnableAdaptiveThrottling)
        {
            return await base.DownloadAsync(request).ConfigureAwait(false);
        }

        var host = request.RequestUri.Host;
        var gate = _throttleManager.GetHostGate(host);
        int attempt = 0;
        while (true)
        {
            attempt++;
            await gate.WaitAsync().ConfigureAwait(false);
            HttpResponseMessage? httpResponseMessage = null;
            HttpRequestMessage? httpRequestMessage = null;
            try
            {
                httpRequestMessage = request.ToHttpRequestMessage();

                var httpClient = await CreateClientAsync(request).ConfigureAwait(false);

                var stopwatch = Stopwatch.StartNew();
                httpResponseMessage = await SendAsync(httpClient, httpRequestMessage).ConfigureAwait(false);
                stopwatch.Stop();
                var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                bool isError = (int)httpResponseMessage.StatusCode >= 400;

                // If downloader overrides HandleAsync, allow it to short-circuit
                var handled = await HandleAsync(request, httpResponseMessage).ConfigureAwait(false);
                Response response;
                if (handled != null)
                {
                    response = handled;
                    if (response.Version == null)
                    {
                        response.Version = HttpVersion.Version11;
                    }
                }
                else
                {
                    response = await httpResponseMessage.ToResponseAsync().ConfigureAwait(false);
                    response.Version = httpResponseMessage.Version;
                }
                response.ElapsedMilliseconds = (int)elapsedMilliseconds;
                response.RequestHash = request.Hash;

                gate.Release(isError, elapsedMilliseconds);
                if (isError && attempt <= _options.MaxRetryAttempts && ShouldRetry(httpResponseMessage))
                {
                    var delay = ComputeRetryDelay(httpResponseMessage, attempt);
                    await Task.Delay(delay).ConfigureAwait(false);
                    continue;
                }
                return response;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{RequestUri} download failed", request.RequestUri);
                gate.Release(true, 0);
                if (attempt <= _options.MaxRetryAttempts)
                {
                    var delay = ComputeRetryDelay(null, attempt);
                    await Task.Delay(delay).ConfigureAwait(false);
                    continue;
                }
                return new Response
                {
                    RequestHash = request.Hash,
                    StatusCode = HttpStatusCode.Gone,
                    ReasonPhrase = ex.ToString(),
                    Version = HttpVersion.Version11
                };
            }
            finally
            {
                ObjectUtilities.DisposeSafely(Logger, httpResponseMessage, httpRequestMessage);
            }
        }
    }

    private bool ShouldRetry(HttpResponseMessage response)
    {
        if (response == null)
        {
            return true;
        }
        var status = (int)response.StatusCode;
        if (status == 429 || status >= 500)
        {
            return true;
        }
        return false;
    }

    private TimeSpan ComputeRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response != null && response.Headers.RetryAfter != null)
        {
            var ra = response.Headers.RetryAfter;
            if (ra.Delta.HasValue)
            {
                return ra.Delta.Value <= _options.MaxRetryDelay ? ra.Delta.Value : _options.MaxRetryDelay;
            }
            if (ra.Date.HasValue)
            {
                var wait = ra.Date.Value - DateTimeOffset.UtcNow;
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }
                if (wait > _options.MaxRetryDelay)
                {
                    wait = _options.MaxRetryDelay;
                }
                return wait;
            }
        }
        // Exponential backoff with jitter
        var backoffMs = Math.Min(_options.MaxRetryDelay.TotalMilliseconds, Math.Pow(2, attempt - 1) * 1000);
        var jitterMs = _rng.NextDouble() * 100;
        var totalMs = Math.Min(_options.MaxRetryDelay.TotalMilliseconds, backoffMs + jitterMs);
        return TimeSpan.FromMilliseconds(totalMs);
    }
}
