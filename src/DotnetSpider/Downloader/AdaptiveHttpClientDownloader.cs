using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetSpider.Downloader;

/// <summary>
/// Downloader that wraps <see cref="HttpClientDownloader"/> with adaptive per-host throttling
/// to tune concurrency and pacing based on live latency/error signals.
/// </summary>
public class AdaptiveHttpClientDownloader : HttpClientDownloader
{
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly AdaptiveThrottleOptions _options;

    public AdaptiveHttpClientDownloader(IHttpClientFactory httpClientFactory,
        IProxyService proxyService,
        IOptions<AdaptiveThrottleOptions> options,
        ILogger<AdaptiveHttpClientDownloader> logger)
        : base(httpClientFactory, proxyService, logger)
    {
        _options = options.Value;
        _throttleManager = new AdaptiveThrottleManager(_options);
    }

    public override async Task<Response> DownloadAsync(Request request)
    {
        var host = request.RequestUri.Host;
        IDisposable permit = await _throttleManager.AcquireAsync(host, CancellationToken.None);
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        try
        {
            var response = await base.DownloadAsync(request);
            stopwatch.Stop();
            var latency = (int)stopwatch.ElapsedMilliseconds;
            var isError = response == null || (int)response.StatusCode >= 400;
            if (response != null)
            {
                response.ElapsedMilliseconds = latency;
            }
            _throttleManager.Record(host, latency, isError);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _throttleManager.Record(host, (int)stopwatch.ElapsedMilliseconds, true);
            Logger.LogError(ex, "{RequestUri} download failed", request.RequestUri);
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
            permit?.Dispose();
        }
    }

    /// <summary>
    /// Expose current stats for a host's gate for observability/testing.
    /// </summary>
    public HostGateStats GetHostStats(string host)
    {
        return _throttleManager.GetHostStats(host);
    }
}
