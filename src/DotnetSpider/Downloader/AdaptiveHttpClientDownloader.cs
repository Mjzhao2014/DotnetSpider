using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using DotnetSpider.Http;
using System.Net.Http;
using DotnetSpider.Proxy;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Downloader;

/// <summary>
/// Wraps the existing <see cref="HttpClientDownloader"/> with per-host adaptive
/// throttling logic based on observed latencies and error rates. Each host gets
/// its own <see cref="HostGate"/> from an <see cref="AdaptiveThrottleManager"/>,
/// allowing fast hosts to ramp up concurrency and slow or error-prone hosts to
/// be crawled more politely.
/// </summary>
public class AdaptiveHttpClientDownloader : HttpClientDownloader
{
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly AdaptiveThrottleOptions _options;

    public AdaptiveHttpClientDownloader(IHttpClientFactory httpClientFactory,
        IProxyService proxyService,
        ILogger<AdaptiveHttpClientDownloader> logger,
        AdaptiveThrottleManager throttleManager,
        AdaptiveThrottleOptions options)
        : base(httpClientFactory, proxyService, logger)
    {
        _throttleManager = throttleManager;
        _options = options;
    }

    public override async Task<Response> DownloadAsync(Request request)
    {
        if (!_options.EnableAdaptiveThrottling)
        {
            return await base.DownloadAsync(request).ConfigureAwait(false);
        }

        var gate = _throttleManager.GetHostGate(request.RequestUri.Host);
        Response response;
        var stopwatch = new Stopwatch();
        using (await gate.AcquireAsync().ConfigureAwait(false))
        {
            stopwatch.Start();
            response = await base.DownloadAsync(request).ConfigureAwait(false);
            stopwatch.Stop();
        }
        // treat non-success status codes as errors for purposes of throttling
        var isError = (int)response.StatusCode >= 400;
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        gate.RecordResult(elapsedMs, isError);
        return response;
    }
}
