using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Downloader;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.Proxy;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Throttling;

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
        _throttleManager = throttleManager ?? throw new ArgumentNullException(nameof(throttleManager));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public new async Task<Response> DownloadAsync(Request request)
    {
        if (!_options.EnableAdaptiveThrottling)
        {
            return await base.DownloadAsync(request);
        }

        var host = request.RequestUri.Host;
        using var throttleLease = await _throttleManager.AcquireAsync(host);

        var stopwatch = new Stopwatch();
        Response response = null;
        var retryCount = 0;

        while (retryCount <= _options.MaxRetryAttempts)
        {
            try
            {
                stopwatch.Restart();
                response = await base.DownloadAsync(request);
                stopwatch.Stop();

                var latencyMs = stopwatch.ElapsedMilliseconds;
                _throttleManager.RecordLatency(host, latencyMs);

                if (IsErrorResponse(response))
                {
                    _throttleManager.RecordError(host);
                    
                    if (ShouldRetry(response, retryCount))
                    {
                        var delay = CalculateRetryDelay(response, retryCount);
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay);
                        }
                        retryCount++;
                        continue;
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _throttleManager.RecordError(host);

                if (retryCount >= _options.MaxRetryAttempts)
                {
                    Logger.LogError(ex, "{RequestUri} download failed after {RetryCount} retries", 
                        request.RequestUri, retryCount);
                    return new Response
                    {
                        RequestHash = request.Hash,
                        StatusCode = HttpStatusCode.Gone,
                        ReasonPhrase = ex.ToString(),
                        Version = HttpVersion.Version11
                    };
                }

                var delay = CalculateRetryDelay(null, retryCount);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }
                retryCount++;
            }
        }

        return response ?? new Response
        {
            RequestHash = request.Hash,
            StatusCode = HttpStatusCode.Gone,
            ReasonPhrase = "Max retries exceeded",
            Version = HttpVersion.Version11
        };
    }

    private static bool IsErrorResponse(Response response)
    {
        if (response == null) return true;
        
        var statusCode = (int)response.StatusCode;
        return statusCode >= 400 || statusCode == 408;
    }

    private bool ShouldRetry(Response response, int retryCount)
    {
        if (retryCount >= _options.MaxRetryAttempts) return false;
        if (response == null) return true;

        var statusCode = (int)response.StatusCode;
        return statusCode == 429 || statusCode == 503 || statusCode == 502 || 
               statusCode == 504 || statusCode == 408;
    }

    private TimeSpan CalculateRetryDelay(Response response, int retryAttempt)
    {
        if (response?.Headers?.TryGetValue("Retry-After", out var retryAfterValue) == true)
        {
            if (int.TryParse(retryAfterValue, out int retryAfterSeconds))
            {
                var retryAfterDelay = TimeSpan.FromSeconds(retryAfterSeconds);
                return retryAfterDelay > _options.MaxRetryDelay ? _options.MaxRetryDelay : retryAfterDelay;
            }
        }

        var baseDelay = TimeSpan.FromMilliseconds(Math.Pow(2, retryAttempt) * 1000);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        var totalDelay = baseDelay + jitter;

        return totalDelay > _options.MaxRetryDelay ? _options.MaxRetryDelay : totalDelay;
    }

    public HostGateStats GetHostStats(string host)
    {
        return _throttleManager.GetHostStats(host);
    }

    public HostGateStats[] GetAllHostStats()
    {
        return _throttleManager.GetAllHostStats();
    }
}