using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.Http;
using DotnetSpider.Proxy;
using DotnetSpider.Throttling;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using StringContent = System.Net.Http.StringContent;

namespace DotnetSpider.Tests.Throttling;

/// <summary>
/// Comprehensive End-to-End tests for Adaptive Throttling feature
/// Tests real-world scenarios with multiple hosts, varying response patterns, and load conditions
/// </summary>
public class AdaptiveThrottlingE2ETests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<IProxyService> _proxyServiceMock;
    private readonly Mock<ILogger<AdaptiveHttpClientDownloader>> _loggerMock;
    private readonly AdaptiveThrottleOptions _options;
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly ConcurrentDictionary<string, List<RequestInfo>> _requestLogs;
    private readonly CancellationTokenSource _globalCts;

    public AdaptiveThrottlingE2ETests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _proxyServiceMock = new Mock<IProxyService>();
        _loggerMock = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        _requestLogs = new ConcurrentDictionary<string, List<RequestInfo>>();
        _globalCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        _options = new AdaptiveThrottleOptions 
        { 
            EnableAdaptiveThrottling = true,
            MaxRetryAttempts = 3,
            MinConcurrency = 1,
            MaxConcurrency = 8,
            CooldownPeriod = TimeSpan.FromMilliseconds(100),
            RequestSpacing = TimeSpan.FromMilliseconds(50),
            EwmaAlpha = 0.3,
            MinLatencyThresholdMs = 100,
            MaxLatencyThresholdMs = 1000,
            ErrorRateThreshold = 0.2,
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        _throttleManager = new AdaptiveThrottleManager(_options);
        _proxyServiceMock.Setup(x => x.GetAsync(It.IsAny<int>())).ReturnsAsync((Uri)null);
    }

    #region E2E Test 1: Per-Host State Management & Isolation

    [Fact]
    public async Task E2E_PerHostStateManagement_IndependentHostBehavior()
    {
        // Arrange: Create different host scenarios
        var fastHostHandler = new E2ETestMessageHandler("fast.com", 
            latencyMs: 50, errorRate: 0.0, statusCode: HttpStatusCode.OK);
        var slowHostHandler = new E2ETestMessageHandler("slow.com", 
            latencyMs: 800, errorRate: 0.1, statusCode: HttpStatusCode.OK);
        var errorProneHandler = new E2ETestMessageHandler("error.com", 
            latencyMs: 200, errorRate: 0.3, statusCode: HttpStatusCode.InternalServerError);

        var combinedHandler = new CompositeMessageHandler();
        combinedHandler.AddHandler("fast.com", fastHostHandler);
        combinedHandler.AddHandler("slow.com", slowHostHandler);
        combinedHandler.AddHandler("error.com", errorProneHandler);

        var httpClient = new HttpClient(combinedHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        // Act: Send requests to different hosts concurrently
        var tasks = new List<Task>();
        var hosts = new[] { "fast.com", "slow.com", "error.com" };
        
        foreach (var host in hosts)
        {
            for (int i = 0; i < 10; i++)
            {
                var request = new Request($"https://{host}/page{i}");
                tasks.Add(ProcessRequestWithLogging(downloader, request, host));
            }
        }

        await Task.WhenAll(tasks);
        await Task.Delay(200); // Allow final adjustments

        // Assert: Verify independent host behavior
        var fastStats = downloader.GetHostStats("fast.com");
        var slowStats = downloader.GetHostStats("slow.com");
        var errorStats = downloader.GetHostStats("error.com");

        // Fast host should have higher concurrency due to low latency
        Assert.True(fastStats.CurrentConcurrency >= slowStats.CurrentConcurrency,
            $"Fast host concurrency ({fastStats.CurrentConcurrency}) should be >= slow host ({slowStats.CurrentConcurrency})");

        // Error-prone host should have lowest concurrency
        Assert.True(errorStats.CurrentConcurrency <= slowStats.CurrentConcurrency,
            $"Error host concurrency ({errorStats.CurrentConcurrency}) should be <= slow host ({slowStats.CurrentConcurrency})");

        // Verify EWMA latency reflects actual host behavior
        Assert.True(fastStats.EwmaLatency < slowStats.EwmaLatency,
            $"Fast host EWMA ({fastStats.EwmaLatency}) should be < slow host EWMA ({slowStats.EwmaLatency})");

        // Verify error rates
        Assert.True(errorStats.ErrorRate > fastStats.ErrorRate,
            $"Error host error rate ({errorStats.ErrorRate}) should be > fast host ({fastStats.ErrorRate})");

        // Verify total requests were processed
        Assert.Equal(10, fastStats.TotalRequests);
        Assert.Equal(10, slowStats.TotalRequests);
        Assert.Equal(10, errorStats.TotalRequests);
    }

    #endregion

    #region E2E Test 2: EWMA Latency Calculation Over Time

    [Fact]
    public async Task E2E_EWMALatencyCalculation_AdaptiveToPatternChanges()
    {
        // Arrange: Create a host that changes behavior over time
        var adaptiveHandler = new AdaptiveLatencyMessageHandler("adaptive.com");
        
        // Initial fast responses (50ms)
        adaptiveHandler.SetLatencyPattern(new[] { 50, 45, 55, 48, 52 });
        
        var httpClient = new HttpClient(adaptiveHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        // Act Phase 1: Fast responses
        for (int i = 0; i < 5; i++)
        {
            var request = new Request($"https://adaptive.com/fast{i}");
            await downloader.DownloadAsync(request);
            await Task.Delay(10);
        }

        var phase1Stats = downloader.GetHostStats("adaptive.com");
        
        // Change to slow responses (500ms)
        adaptiveHandler.SetLatencyPattern(new[] { 500, 520, 480, 510, 495 });
        
        // Act Phase 2: Slow responses
        for (int i = 0; i < 10; i++)
        {
            var request = new Request($"https://adaptive.com/slow{i}");
            await downloader.DownloadAsync(request);
            await Task.Delay(10);
        }

        var phase2Stats = downloader.GetHostStats("adaptive.com");

        // Assert: EWMA should adapt from fast to slow
        Assert.True(phase1Stats.EwmaLatency < 100, 
            $"Phase 1 EWMA ({phase1Stats.EwmaLatency}) should reflect fast responses");
        Assert.True(phase2Stats.EwmaLatency > 300, 
            $"Phase 2 EWMA ({phase2Stats.EwmaLatency}) should reflect slow responses");
        
        // Verify concurrency adapted accordingly
        Assert.True(phase2Stats.CurrentConcurrency <= phase1Stats.CurrentConcurrency,
            $"Concurrency should decrease from phase 1 ({phase1Stats.CurrentConcurrency}) to phase 2 ({phase2Stats.CurrentConcurrency})");
    }

    #endregion

    #region E2E Test 3: Adaptive Concurrency Under Load

    [Fact]
    public async Task E2E_AdaptiveConcurrency_RespondsToLoadPatterns()
    {
        // Arrange: Create a load-sensitive handler
        var loadHandler = new LoadSensitiveMessageHandler("load.com");
        var httpClient = new HttpClient(loadHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var concurrencyHistory = new List<(DateTime timestamp, int concurrency)>();
        
        // Act: Generate sustained load
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            var request = new Request($"https://load.com/item{i}");
            tasks.Add(Task.Run(async () =>
            {
                await downloader.DownloadAsync(request);
                
                // Record concurrency at various points
                if (i % 5 == 0)
                {
                    var stats = downloader.GetHostStats("load.com");
                    lock (concurrencyHistory)
                    {
                        concurrencyHistory.Add((DateTime.UtcNow, stats?.CurrentConcurrency ?? 1));
                    }
                }
            }));
            
            // Stagger request initiation
            if (i % 3 == 0) await Task.Delay(20);
        }

        await Task.WhenAll(tasks);
        
        // Assert: Verify adaptive behavior
        var finalStats = downloader.GetHostStats("load.com");
        Assert.NotNull(finalStats);
        
        // Should have processed all requests
        Assert.Equal(50, finalStats.TotalRequests);
        
        // Concurrency should be within configured bounds
        Assert.True(finalStats.CurrentConcurrency >= _options.MinConcurrency);
        Assert.True(finalStats.CurrentConcurrency <= _options.MaxConcurrency);
        
        // Should show some concurrency variation over time
        if (concurrencyHistory.Count >= 3)
        {
            var uniqueConcurrencyValues = concurrencyHistory.Select(h => h.concurrency).Distinct().Count();
            Assert.True(uniqueConcurrencyValues > 1, "Concurrency should adapt over time");
        }
    }

    #endregion

    #region E2E Test 4: Error Handling & Retry Logic with Backoff

    [Fact]
    public async Task E2E_ErrorHandling_RetryWithBackoffAndRecovery()
    {
        // Arrange: Create handler that simulates service recovery
        var recoveryHandler = new ServiceRecoveryMessageHandler("recovery.com");
        // First 3 requests fail, then succeed
        recoveryHandler.SetErrorPattern(new[] { true, true, true, false, false, false, false });
        
        var httpClient = new HttpClient(recoveryHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var stopwatch = Stopwatch.StartNew();
        
        // Act: Make request that requires retries
        var request = new Request("https://recovery.com/failing-service");
        var response = await downloader.DownloadAsync(request);
        
        stopwatch.Stop();

        // Assert: Verify retry behavior
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        // Should have taken time due to retries with backoff
        Assert.True(stopwatch.ElapsedMilliseconds > 1000, 
            $"Request should take time due to retries, actual: {stopwatch.ElapsedMilliseconds}ms");
        
        var stats = downloader.GetHostStats("recovery.com");
        Assert.True(stats.TotalErrors >= 3, "Should have recorded error attempts");
        Assert.True(stats.ErrorRate > 0, "Should have positive error rate initially");
        
        // Make additional successful requests to test recovery
        for (int i = 0; i < 5; i++)
        {
            var successRequest = new Request($"https://recovery.com/success{i}");
            var successResponse = await downloader.DownloadAsync(successRequest);
            Assert.Equal(HttpStatusCode.OK, successResponse.StatusCode);
        }
        
        var recoveredStats = downloader.GetHostStats("recovery.com");
        Assert.True(recoveredStats.ErrorRate < stats.ErrorRate, "Error rate should improve after successful requests");
    }

    #endregion

    #region E2E Test 5: Request Spacing Under High Frequency

    [Fact]
    public async Task E2E_RequestSpacing_EnforcesMinimumDelayPerHost()
    {
        // Arrange: Fast handler to test spacing enforcement
        var spacingHandler = new TimestampTrackingMessageHandler("spacing.com");
        var httpClient = new HttpClient(spacingHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        // Act: Send rapid sequential requests
        var timestamps = new List<DateTime>();
        for (int i = 0; i < 5; i++)
        {
            timestamps.Add(DateTime.UtcNow);
            var request = new Request($"https://spacing.com/rapid{i}");
            await downloader.DownloadAsync(request);
        }

        // Assert: Verify minimum spacing was enforced
        for (int i = 1; i < timestamps.Count; i++)
        {
            var gap = timestamps[i] - timestamps[i - 1];
            // Allow some tolerance for test execution overhead
            Assert.True(gap >= TimeSpan.FromMilliseconds(_options.RequestSpacing.TotalMilliseconds - 10),
                $"Request spacing should be at least {_options.RequestSpacing.TotalMilliseconds}ms, but was {gap.TotalMilliseconds}ms");
        }
    }

    #endregion

    #region E2E Test 6: Cancellation Safety

    [Fact]
    public async Task E2E_CancellationSafety_ProperlyCancelsAndCleansUp()
    {
        // Arrange: Slow handler to test cancellation
        var slowHandler = new E2ETestMessageHandler("cancel.com", 
            latencyMs: 2000, errorRate: 0.0, statusCode: HttpStatusCode.OK);
        var httpClient = new HttpClient(slowHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        // Act & Assert: Test cancellation during request
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var hostGate = _throttleManager.GetHostGate("cancel.com");
        var initialConcurrency = hostGate.CurrentConcurrency;

        var cancellationTasks = new List<Task>();
        for (int i = 0; i < 3; i++)
        {
            cancellationTasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var lease = await hostGate.AcquireAsync(cts.Token);
                    await Task.Delay(1000, cts.Token); // This should be cancelled
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }));
        }

        // Wait for cancellation
        await Task.Delay(600);
        
        // Verify resources are properly released
        await Task.Delay(100); // Allow cleanup
        var finalConcurrency = hostGate.CurrentConcurrency;
        Assert.Equal(initialConcurrency, finalConcurrency);
    }

    #endregion

    #region E2E Test 7: Retry-After Header Handling

    [Fact]
    public async Task E2E_RetryAfterHeaders_HonorsServerDirectives()
    {
        // Arrange: Handler that sends Retry-After headers
        var retryAfterHandler = new RetryAfterMessageHandler("retry.com");
        var httpClient = new HttpClient(retryAfterHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        var stopwatch = Stopwatch.StartNew();
        
        // Act: Make request that returns 429 with Retry-After
        var request = new Request("https://retry.com/rate-limited");
        var response = await downloader.DownloadAsync(request);
        
        stopwatch.Stop();

        // Assert: Should have honored the Retry-After delay
        Assert.True(stopwatch.ElapsedMilliseconds >= 1900, // 2 seconds minus some tolerance
            $"Should have waited for Retry-After delay, actual: {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region E2E Test 8: Mixed Workload Realistic Scenario

    [Fact]
    public async Task E2E_MixedWorkload_RealisticCrawlingScenario()
    {
        // Arrange: Multiple hosts with different characteristics
        var newsHandler = new E2ETestMessageHandler("news.com", 120, 0.05, HttpStatusCode.OK);
        var socialHandler = new E2ETestMessageHandler("social.com", 200, 0.1, HttpStatusCode.OK);
        var apiHandler = new RateLimitedMessageHandler("api.service.com");
        var cmsHandler = new E2ETestMessageHandler("slow-cms.com", 800, 0.15, HttpStatusCode.OK);
        var unreliableHandler = new E2ETestMessageHandler("unreliable.com", 300, 0.3, HttpStatusCode.InternalServerError);

        var compositeHandler = new CompositeMessageHandler();
        compositeHandler.AddHandler("news.com", newsHandler);
        compositeHandler.AddHandler("social.com", socialHandler);
        compositeHandler.AddHandler("api.service.com", apiHandler);
        compositeHandler.AddHandler("slow-cms.com", cmsHandler);
        compositeHandler.AddHandler("unreliable.com", unreliableHandler);

        var httpClient = new HttpClient(compositeHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            _proxyServiceMock.Object,
            _loggerMock.Object,
            _throttleManager,
            _options);

        // Act: Simulate realistic crawling pattern
        var allTasks = new List<Task>();
        var hosts = new[] { "news.com", "social.com", "api.service.com", "slow-cms.com", "unreliable.com" };
        
        // Generate mixed workload over time
        for (int round = 0; round < 5; round++)
        {
            foreach (var host in hosts)
            {
                for (int i = 0; i < 4; i++)
                {
                    var request = new Request($"https://{host}/page{round}-{i}");
                    allTasks.Add(ProcessRequestWithLogging(downloader, request, host));
                }
            }
            
            if (round < 4) await Task.Delay(50); // Brief pause between rounds
        }

        await Task.WhenAll(allTasks);
        await Task.Delay(200); // Allow final state settling

        // Assert: Verify realistic adaptive behavior
        var allStats = downloader.GetAllHostStats();
        Assert.Equal(5, allStats.Length);

        foreach (var stats in allStats)
        {
            // All hosts should have processed requests
            Assert.True(stats.TotalRequests >= 15, 
                $"Host {stats.Host} should have processed requests, actual: {stats.TotalRequests}");
            
            // Concurrency should be within bounds
            Assert.True(stats.CurrentConcurrency >= _options.MinConcurrency);
            Assert.True(stats.CurrentConcurrency <= _options.MaxConcurrency);
            
            // EWMA should be reasonable
            Assert.True(stats.EwmaLatency > 0);
        }

        // Fast hosts should generally have higher concurrency than slow/unreliable ones
        var newsStats = allStats.First(s => s.Host == "news.com");
        var slowStats = allStats.First(s => s.Host == "slow-cms.com");
        var unreliableStats = allStats.First(s => s.Host == "unreliable.com");

        Assert.True(newsStats.CurrentConcurrency >= unreliableStats.CurrentConcurrency,
            "Reliable fast host should have higher concurrency than unreliable host");
        Assert.True(slowStats.EwmaLatency > newsStats.EwmaLatency,
            "Slow host should have higher EWMA latency");
    }

    #endregion

    #region Helper Classes and Methods

    private async Task ProcessRequestWithLogging(AdaptiveHttpClientDownloader downloader, Request request, string host)
    {
        var startTime = DateTime.UtcNow;
        try
        {
            var response = await downloader.DownloadAsync(request);
            var endTime = DateTime.UtcNow;
            
            var requestInfo = new RequestInfo
            {
                Url = request.RequestUri.ToString(),
                StartTime = startTime,
                EndTime = endTime,
                StatusCode = response.StatusCode,
                Success = (int)response.StatusCode < 400
            };

            _requestLogs.AddOrUpdate(host, 
                new List<RequestInfo> { requestInfo },
                (key, list) => { list.Add(requestInfo); return list; });
        }
        catch (Exception ex)
        {
            var requestInfo = new RequestInfo
            {
                Url = request.RequestUri.ToString(),
                StartTime = startTime,
                EndTime = DateTime.UtcNow,
                StatusCode = HttpStatusCode.InternalServerError,
                Success = false,
                Exception = ex
            };

            _requestLogs.AddOrUpdate(host, 
                new List<RequestInfo> { requestInfo },
                (key, list) => { list.Add(requestInfo); return list; });
        }
    }

    public void Dispose()
    {
        _throttleManager?.Dispose();
        _globalCts?.Dispose();
    }

    #endregion
}

#region Test Infrastructure Classes

public class RequestInfo
{
    public string Url { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public HttpStatusCode StatusCode { get; set; }
    public bool Success { get; set; }
    public Exception Exception { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
}

public class E2ETestMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private readonly int _latencyMs;
    private readonly double _errorRate;
    private readonly HttpStatusCode _statusCode;
    private readonly Random _random = new();
    private int _requestCount;

    public E2ETestMessageHandler(string host, int latencyMs, double errorRate, HttpStatusCode statusCode)
    {
        _host = host;
        _latencyMs = latencyMs;
        _errorRate = errorRate;
        _statusCode = statusCode;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        
        // Simulate network latency with some variance
        var actualLatency = _latencyMs + _random.Next(-_latencyMs / 4, _latencyMs / 4);
        await Task.Delay(Math.Max(10, actualLatency), cancellationToken);

        // Simulate error rate
        if (_random.NextDouble() < _errorRate)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"Simulated error for {_host}")
            };
        }

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent($"Success response from {_host} (request #{_requestCount})")
        };
    }
}

public class CompositeMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, HttpMessageHandler> _handlers = new();

    public void AddHandler(string host, HttpMessageHandler handler)
    {
        _handlers[host] = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri.Host;
        if (_handlers.TryGetValue(host, out var handler))
        {
            // Use reflection to call the protected SendAsync method
            var method = typeof(HttpMessageHandler).GetMethod("SendAsync", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var task = (Task<HttpResponseMessage>)method.Invoke(handler, new object[] { request, cancellationToken });
            return await task;
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No handler configured for {host}")
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var handler in _handlers.Values)
            {
                handler?.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

public class AdaptiveLatencyMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private int[] _latencyPattern;
    private int _patternIndex;

    public AdaptiveLatencyMessageHandler(string host)
    {
        _host = host;
        _latencyPattern = new[] { 100 }; // Default
    }

    public void SetLatencyPattern(int[] latencies)
    {
        _latencyPattern = latencies;
        _patternIndex = 0;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var latency = _latencyPattern[_patternIndex % _latencyPattern.Length];
        _patternIndex++;

        await Task.Delay(latency, cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"Response from {_host} with {latency}ms latency")
        };
    }
}

public class LoadSensitiveMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private int _currentLoad;
    private readonly Random _random = new();

    public LoadSensitiveMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var currentLoad = Interlocked.Increment(ref _currentLoad);
        
        try
        {
            // Latency increases with concurrent load
            var baseLatency = 100;
            var loadLatency = Math.Min(currentLoad * 50, 500); // Cap at 500ms
            var totalLatency = baseLatency + loadLatency;

            await Task.Delay(totalLatency, cancellationToken);

            // Higher chance of errors under high load
            var errorProbability = Math.Min(currentLoad * 0.02, 0.2);
            if (_random.NextDouble() < errorProbability)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent($"Service overloaded on {_host}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"Response from {_host} under load {currentLoad}")
            };
        }
        finally
        {
            Interlocked.Decrement(ref _currentLoad);
        }
    }
}

public class ServiceRecoveryMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private bool[] _errorPattern;
    private int _requestIndex;

    public ServiceRecoveryMessageHandler(string host)
    {
        _host = host;
    }

    public void SetErrorPattern(bool[] shouldError)
    {
        _errorPattern = shouldError;
        _requestIndex = 0;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken); // Simulate processing time

        var shouldError = _requestIndex < _errorPattern.Length && _errorPattern[_requestIndex];
        _requestIndex++;

        if (shouldError)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent($"Service temporarily unavailable on {_host}")
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"Service recovered on {_host}")
        };
    }
}

public class TimestampTrackingMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private readonly List<DateTime> _requestTimes = new();

    public TimestampTrackingMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requestTimes)
        {
            _requestTimes.Add(DateTime.UtcNow);
        }

        await Task.Delay(10, cancellationToken); // Minimal processing time

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"Response from {_host}")
        };
    }

    public List<DateTime> GetRequestTimes()
    {
        lock (_requestTimes)
        {
            return new List<DateTime>(_requestTimes);
        }
    }
}

public class RetryAfterMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private bool _firstRequest = true;

    public RetryAfterMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(50, cancellationToken);

        if (_firstRequest)
        {
            _firstRequest = false;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("Rate limited")
            };
            response.Headers.Add("Retry-After", "2"); // 2 seconds
            return response;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"Success after retry-after on {_host}")
        };
    }
}

public class RateLimitedMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private int _requestCount;
    private DateTime _windowStart = DateTime.UtcNow;

    public RateLimitedMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);

        // Reset window every 10 seconds
        var now = DateTime.UtcNow;
        if (now - _windowStart > TimeSpan.FromSeconds(10))
        {
            _requestCount = 0;
            _windowStart = now;
        }

        _requestCount++;

        // Allow 5 requests per window, then rate limit
        if (_requestCount > 5)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent($"Rate limited on {_host}")
            };
            response.Headers.Add("Retry-After", "1");
            return response;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"API response from {_host}")
        };
    }
}

#endregion
