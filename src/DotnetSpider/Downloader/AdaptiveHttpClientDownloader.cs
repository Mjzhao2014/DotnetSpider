using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.Proxy;
using DotnetSpider.Throttling;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Downloader;

public class AdaptiveHttpClientDownloader : IDownloader, IDisposable
{
    private readonly IHostThrottler _hostThrottler;
    private readonly AdaptiveDownloaderOptions _options;
    private readonly IProxyService _proxyService;
    protected IHttpClientFactory HttpClientFactory { get; }
    protected ILogger Logger { get; }
    protected bool UseProxy { get; }

    public AdaptiveHttpClientDownloader(
        IHttpClientFactory httpClientFactory,
        IProxyService proxyService,
        ILogger<AdaptiveHttpClientDownloader> logger,
        IHostThrottler hostThrottler,
        AdaptiveDownloaderOptions options = null)
    {
        HttpClientFactory = httpClientFactory;
        Logger = logger;
        _proxyService = proxyService;
        UseProxy = !(_proxyService is EmptyProxyService);
        _hostThrottler = hostThrottler ?? throw new ArgumentNullException(nameof(hostThrottler));
        _options = options ?? new AdaptiveDownloaderOptions();
    }

    public async Task<Response> DownloadAsync(Request request)
    {
        var host = request.RequestUri.Host;
        var stopwatch = new Stopwatch();
        IDisposable throttleToken = null;
        
        HttpResponseMessage httpResponseMessage = null;
        HttpRequestMessage httpRequestMessage = null;
        
        try
        {
            throttleToken = await _hostThrottler.AcquireAsync(host);
            
            httpRequestMessage = request.ToHttpRequestMessage();
            var httpClient = await CreateClientAsync(request);
            
            stopwatch.Start();
            httpResponseMessage = await SendAsync(httpClient, httpRequestMessage);
            stopwatch.Stop();
            
            var latency = stopwatch.Elapsed;
            var response = await HandleAsync(request, httpResponseMessage);
            
            if (response != null)
            {
                response.Version = response.Version == null ? HttpVersion.Version11 : response.Version;
                
                if (IsSuccessResponse(response))
                {
                    _hostThrottler.RecordSuccess(host, latency);
                    Logger.LogDebug("Request to {Host} succeeded in {Latency}ms", host, latency.TotalMilliseconds);
                }
                else
                {
                    _hostThrottler.RecordError(host, latency);
                    Logger.LogWarning("Request to {Host} failed with status {StatusCode} in {Latency}ms", 
                        host, response.StatusCode, latency.TotalMilliseconds);
                }
                
                return response;
            }

            response = await httpResponseMessage.ToResponseAsync();
            response.ElapsedMilliseconds = (int)latency.TotalMilliseconds;
            response.RequestHash = request.Hash;
            response.Version = httpResponseMessage.Version;
            
            if (IsSuccessResponse(response))
            {
                _hostThrottler.RecordSuccess(host, latency);
                Logger.LogDebug("Request to {Host} succeeded in {Latency}ms", host, latency.TotalMilliseconds);
            }
            else
            {
                _hostThrottler.RecordError(host, latency);
                Logger.LogWarning("Request to {Host} failed with status {StatusCode} in {Latency}ms", 
                    host, response.StatusCode, latency.TotalMilliseconds);
            }

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var latency = stopwatch.Elapsed;
            
            _hostThrottler.RecordError(host, latency);
            Logger.LogError(ex, "Request to {Host} threw exception after {Latency}ms", host, latency.TotalMilliseconds);
            
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
            throttleToken?.Dispose();
            ObjectUtilities.DisposeSafely(Logger, httpResponseMessage, httpRequestMessage);
        }
    }

    protected virtual async Task<HttpResponseMessage> SendAsync(HttpClient httpClient, HttpRequestMessage httpRequestMessage)
    {
        if (_options.MaxBandwidthPerHost.HasValue)
        {
            var originalContent = httpRequestMessage.Content;
            if (originalContent != null)
            {
                var contentBytes = await originalContent.ReadAsByteArrayAsync();
                var throttledStream = new BandwidthThrottledStream(
                    new System.IO.MemoryStream(contentBytes), 
                    _options.MaxBandwidthPerHost.Value);
                
                httpRequestMessage.Content = new StreamContent(throttledStream);
                
                if (originalContent.Headers != null)
                {
                    foreach (var header in originalContent.Headers)
                    {
                        httpRequestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }
        }

        var response = await httpClient.SendAsync(httpRequestMessage);
        
        if (_options.MaxBandwidthPerHost.HasValue && response.Content != null)
        {
            var originalStream = await response.Content.ReadAsStreamAsync();
            var throttledStream = new BandwidthThrottledStream(originalStream, _options.MaxBandwidthPerHost.Value);
            response.Content = new StreamContent(throttledStream);
        }
        
        return response;
    }

    protected virtual async Task<HttpClient> CreateClientAsync(Request request)
    {
        string name;
        if (UseProxy)
        {
            var proxy = await _proxyService.GetAsync(request.Timeout);
            if (proxy == null)
            {
                throw new SpiderException("获取代理失败");
            }

            name = $"{Const.ProxyPrefix}{proxy}";
        }
        else
        {
            name = request.RequestUri.Host;
        }

        return HttpClientFactory.CreateClient(name);
    }

    protected virtual Task<Response> HandleAsync(Request request, HttpResponseMessage responseMessage)
    {
        return Task.FromResult((Response)null);
    }

    private static bool IsSuccessResponse(Response response)
    {
        var statusCode = (int)response.StatusCode;
        return statusCode >= 200 && statusCode < 400;
    }

    public HostMetrics GetHostMetrics(string host)
    {
        return _hostThrottler.GetMetrics(host);
    }

    public void Dispose()
    {
        _hostThrottler?.Dispose();
    }
}

public class AdaptiveDownloaderOptions
{
    public long? MaxBandwidthPerHost { get; set; }
    public bool EnableAdaptiveThrottling { get; set; } = true;
    public bool EnableBandwidthThrottling { get; set; } = true;
}