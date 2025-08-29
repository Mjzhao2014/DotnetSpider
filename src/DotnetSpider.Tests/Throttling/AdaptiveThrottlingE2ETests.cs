using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetSpider.DataFlow;
using DotnetSpider.DataFlow.Parser;
using DotnetSpider.Downloader;
using DotnetSpider.Http;
using DotnetSpider.Infrastructure;
using DotnetSpider.MessageQueue;
using DotnetSpider.Proxy;
using DotnetSpider.Scheduler;
using DotnetSpider.Scheduler.Component;
using DotnetSpider.Selector;
using DotnetSpider.Statistic;
using DotnetSpider.Statistic.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
            RequestSpacing = TimeSpan.FromMilliseconds(500),
            EwmaAlpha = 0.3,
            MinLatencyThresholdMs = 100,
            MaxLatencyThresholdMs = 1000,
            ErrorRateThreshold = 0.2,
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        _throttleManager = new AdaptiveThrottleManager(_options);
    }

    [Fact]
    public async Task E2E_PerHostStateManagement_IndependentHostBehavior()
    {
        // Arrange: Create different host scenarios with proper page content
        var fastHostHandler = new MockedWebsiteHandler("fast.com", new MockSiteConfig
        {
            LatencyMs = 50,
            ErrorRate = 0.0,
            StatusCode = HttpStatusCode.OK,
            SiteName = "Fast News",
            ContentTemplate = "<html><head><title>Fast News - {PAGE}</title></head>" +
                            "<body><h1>Fast News Site</h1><p>This is {PAGE} with fast loading content.</p>" +
                            "<nav><a href='/page{NEXT}'>Next Page</a></nav></body></html>"
        });
        
        var slowHostHandler = new MockedWebsiteHandler("slow.com", new MockSiteConfig
        {
            LatencyMs = 800,
            ErrorRate = 0.1,
            StatusCode = HttpStatusCode.OK,
            SiteName = "Slow CMS",
            ContentTemplate = "<html><head><title>Slow CMS - {PAGE}</title></head>" +
                            "<body><h1>Slow Content Management System</h1><p>Loading {PAGE} slowly...</p>" +
                            "<div>Heavy content with lots of data processing...</div></body></html>"
        });
        
        var errorProneHandler = new MockedWebsiteHandler("error.com", new MockSiteConfig
        {
            LatencyMs = 200,
            ErrorRate = 0.3,
            StatusCode = HttpStatusCode.OK, // Will be overridden by error rate
            SiteName = "Unreliable Service",
            ContentTemplate = "<html><head><title>Unreliable Service - {PAGE}</title></head>" +
                            "<body><h1>Unreliable Service</h1><p>This service fails often for {PAGE}</p></body></html>",
            ErrorContent = "<html><head><title>Service Error</title></head>" +
                         "<body><h1>500 Internal Server Error</h1><p>Service temporarily unavailable</p></body></html>"
        });

        var combinedHandler = new CompositeMessageHandler();
        combinedHandler.AddHandler("fast.com", fastHostHandler);
        combinedHandler.AddHandler("slow.com", slowHostHandler);
        combinedHandler.AddHandler("error.com", errorProneHandler);

        var httpClient = new HttpClient(combinedHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            new EmptyProxyService(),
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

        // ErrorRate > 0, 10<=totalrequest <=13 
        Assert.True(slowStats.TotalRequests >= 10 && slowStats.TotalRequests <= 10 + _options.MaxRetryAttempts);
        Assert.True(errorStats.TotalRequests >= 10  && errorStats.TotalRequests <= 10 + _options.MaxRetryAttempts);
    }

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
            new EmptyProxyService(),
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

    [Fact]
    public async Task E2E_AdaptiveConcurrency_RespondsToLoadPatterns()
    {
        // Arrange: Create special options for this test to ensure the increase branch can fire
        var loadTestOptions = new AdaptiveThrottleOptions 
        { 
            EnableAdaptiveThrottling = true,
            MaxRetryAttempts = 3,
            MinConcurrency = 1,
            MaxConcurrency = 8,
            RequestSpacing = TimeSpan.FromMilliseconds(500),
            EwmaAlpha = 0.3,
            MinLatencyThresholdMs = 200, // Increased from 100 to allow increase branch to fire
            MaxLatencyThresholdMs = 1000,
            ErrorRateThreshold = 0.2,
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        var loadTestThrottleManager = new AdaptiveThrottleManager(loadTestOptions);

        // Create a load-sensitive handler (base latency = 100ms)
        var loadHandler = new LoadSensitiveMessageHandler("load.com");
        var httpClient = new HttpClient(loadHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            new EmptyProxyService(),
            _loggerMock.Object,
            loadTestThrottleManager,
            loadTestOptions);

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
        
        // Should have processed at least 50 requests (some may be retries due to load sensitivity)
        Assert.True(finalStats.TotalRequests >= 50, 
            $"Should have processed at least 50 requests, but got {finalStats.TotalRequests}");
        
        // Concurrency should be within configured bounds
        Assert.True(finalStats.CurrentConcurrency >= loadTestOptions.MinConcurrency);
        Assert.True(finalStats.CurrentConcurrency <= loadTestOptions.MaxConcurrency);
        
        // Should show some concurrency variation over time
        if (concurrencyHistory.Count >= 3)
        {
            var uniqueConcurrencyValues = concurrencyHistory.Select(h => h.concurrency).Distinct().Count();
            Assert.True(uniqueConcurrencyValues > 1, "Concurrency should adapt over time");
        }
    }

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
            new EmptyProxyService(),
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

    [Fact]
    public async Task E2E_RequestSpacing_EnforcesMinimumDelayPerHost()
    {
        // Arrange: Configure with specific spacing and higher concurrency to expose race condition
        var spacingOptions = new AdaptiveThrottleOptions
        {
            EnableAdaptiveThrottling = true,
            MinConcurrency = 3,
            MaxConcurrency = 5, // Allow multiple concurrent requests
            RequestSpacing = TimeSpan.FromMilliseconds(200), // Require 200ms spacing
            EwmaAlpha = 0.3,
            MaxRetryAttempts = 1
        };
        var spacingManager = new AdaptiveThrottleManager(spacingOptions);

        // Use a fast handler that responds quickly to highlight spacing violations
        var spacingHandler = new TimestampTrackingMessageHandler("spacing.com");
        var httpClient = new HttpClient(spacingHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            new EmptyProxyService(),
            _loggerMock.Object,
            spacingManager,
            spacingOptions);

        // Act: Send concurrent requests that should trigger the race condition
        var tasks = new List<Task>();
        var requestCount = 10;
        
        // Launch all requests concurrently to maximize race condition potential
        for (int i = 0; i < requestCount; i++)
        {
            var request = new Request($"https://spacing.com/concurrent{i}");
            tasks.Add(Task.Run(async () => await downloader.DownloadAsync(request)));
        }

        var startTime = DateTime.UtcNow;
        await Task.WhenAll(tasks);
        var endTime = DateTime.UtcNow;

        // Assert: Verify minimum spacing was enforced under concurrent conditions
        var actualTimestamps = spacingHandler.GetRequestTimes();
        Assert.True(actualTimestamps.Count >= requestCount, 
            $"Should have recorded at least {requestCount} request timestamps, got {actualTimestamps.Count}");

        // Sort timestamps to check spacing in chronological order
        actualTimestamps.Sort();

        // Check that minimum spacing is enforced between consecutive requests
        var violations = new List<string>();
        var minSpacingMs = spacingOptions.RequestSpacing.TotalMilliseconds;
        
        for (int i = 1; i < actualTimestamps.Count; i++)
        {
            var gap = actualTimestamps[i] - actualTimestamps[i - 1];
            var gapMs = gap.TotalMilliseconds;
            
            // Allow for some measurement precision but be strict about the spacing
            // If gap is less than 90% of required spacing, it's a clear violation
            if (gapMs < minSpacingMs * 0.9)
            {
                violations.Add($"Gap {i}: {gapMs:F1}ms (required: {minSpacingMs}ms)");
            }
        }

        Assert.True(violations.Count == 0, 
            $"Request spacing violations detected in concurrent execution:\n{string.Join("\n", violations)}\n" +
            $"This indicates a race condition in HostGate._lastRequestTime handling.\n" +
            $"Total requests: {actualTimestamps.Count}, Violations: {violations.Count}");

        // Additional verification: Total execution time should reflect spacing constraints
        var expectedMinDuration = TimeSpan.FromMilliseconds((requestCount - 1) * minSpacingMs * 0.8); // 80% of theoretical minimum
        var actualDuration = endTime - startTime;
        
        Assert.True(actualDuration >= expectedMinDuration,
            $"Total execution time ({actualDuration.TotalMilliseconds:F0}ms) should reflect spacing constraints " +
            $"(expected minimum: {expectedMinDuration.TotalMilliseconds:F0}ms)");

        spacingManager.Dispose();
    }

   

    [Fact]
    public async Task E2E_RetryAfterHeaders_HonorsServerDirectives()
    {
        // Arrange: Handler that sends Retry-After headers.
        var retryAfterHandler = new RetryAfterMessageHandler("retry.com");
        var httpClient = new HttpClient(retryAfterHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            new EmptyProxyService(),
            _loggerMock.Object,
            _throttleManager,
            _options);

        var stopwatch = Stopwatch.StartNew();
        
        // Act: Make request that returns 429 with Retry-After
        var request = new Request("https://retry.com/rate-limited");
        var response = await downloader.DownloadAsync(request);
        
        stopwatch.Stop();

        // Assert: Should have honored the Retry-After delay
        Assert.True(stopwatch.ElapsedMilliseconds >= 9900, // 10 seconds minus some tolerance
            $"Should have waited for Retry-After delay, actual: {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task E2E_MixedWorkload_RealisticCrawlingScenario()
    {
        // Arrange: Multiple hosts with different characteristics and realistic content
        var newsHandler = new MockedWebsiteHandler("news.com", new MockSiteConfig
        {
            LatencyMs = 120,
            ErrorRate = 0.05,
            StatusCode = HttpStatusCode.OK,
            SiteName = "Fast News Network",
            ContentTemplate = "<html><head><title>News - {PAGE}</title></head>" +
                            "<body><h1>Breaking News</h1><article><h2>{PAGE}</h2>" +
                            "<p>Latest news content here...</p></article>" +
                            "<nav><a href='/page{NEXT}'>More News</a></nav></body></html>"
        });
        
        var socialHandler = new MockedWebsiteHandler("social.com", new MockSiteConfig
        {
            LatencyMs = 200,
            ErrorRate = 0.1,
            StatusCode = HttpStatusCode.OK,
            SiteName = "Social Network",
            ContentTemplate = "<html><head><title>Social - {PAGE}</title></head>" +
                            "<body><h1>Social Feed</h1><div class='posts'>{PAGE} posts...</div>" +
                            "<script>loadMorePosts();</script></body></html>"
        });
        
        var apiHandler = new RateLimitedApiHandler("api.service.com");
        
        var cmsHandler = new MockedWebsiteHandler("slow-cms.com", new MockSiteConfig
        {
            LatencyMs = 800,
            ErrorRate = 0.15,
            StatusCode = HttpStatusCode.OK,
            SiteName = "Enterprise CMS",
            ContentTemplate = "<html><head><title>CMS - {PAGE}</title></head>" +
                            "<body><h1>Content Management</h1>" +
                            "<div class='heavy-content'>{PAGE} with lots of server-side processing...</div>" +
                            "<footer>Powered by Heavy CMS</footer></body></html>"
        });
        
        var unreliableHandler = new MockedWebsiteHandler("unreliable.com", new MockSiteConfig
        {
            LatencyMs = 300,
            ErrorRate = 0.3,
            StatusCode = HttpStatusCode.OK,
            SiteName = "Unreliable Service",
            ContentTemplate = "<html><head><title>Unstable - {PAGE}</title></head>" +
                            "<body><h1>Unstable Service</h1><p>This might work for {PAGE}...</p></body></html>",
            ErrorContent = "<html><head><title>Service Down</title></head>" +
                         "<body><h1>503 Service Unavailable</h1><p>Please try again later</p></body></html>"
        });

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
            new EmptyProxyService(),
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

    [Fact]
    public async Task E2E_RetryLogic_TimeoutsAndErrorCodes_RespectsMaxRetryAttempts()
    {
        // Arrange: Create handlers that simulate different error scenarios
        // Note: Only timeouts, 429, and 5xx errors are retryable; 403/404 are not retryable
        var timeoutHandler = new TimeoutErrorMessageHandler("timeout.com");
        var rateLimitHandler = new RateLimitErrorMessageHandler("ratelimit.com");
        var serverErrorHandler = new ServerErrorMessageHandler("servererror.com");
        var forbiddenHandler = new ForbiddenErrorMessageHandler("forbidden.com");
        var notFoundHandler = new NotFoundErrorMessageHandler("notfound.com");

        var combinedHandler = new CompositeMessageHandler();
        combinedHandler.AddHandler("timeout.com", timeoutHandler);
        combinedHandler.AddHandler("ratelimit.com", rateLimitHandler);
        combinedHandler.AddHandler("servererror.com", serverErrorHandler);
        combinedHandler.AddHandler("forbidden.com", forbiddenHandler);
        combinedHandler.AddHandler("notfound.com", notFoundHandler);

        var httpClient = new HttpClient(combinedHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            new EmptyProxyService(),
            _loggerMock.Object,
            _throttleManager,
            _options);

        var results = new List<(string url, HttpStatusCode statusCode, bool success, int attempts, TimeSpan duration)>();

        // Act: Test timeout scenario
        var timeoutStopwatch = Stopwatch.StartNew();
        try
        {
            var timeoutRequest = new Request("https://timeout.com/test");
            var timeoutResponse = await downloader.DownloadAsync(timeoutRequest);
            timeoutStopwatch.Stop();
            
            results.Add(("timeout.com", timeoutResponse.StatusCode, true, 
                timeoutHandler.AttemptCount, timeoutStopwatch.Elapsed));
        }
        catch (Exception)
        {
            timeoutStopwatch.Stop();
            results.Add(("timeout.com", HttpStatusCode.RequestTimeout, false, 
                timeoutHandler.AttemptCount, timeoutStopwatch.Elapsed));
        }

        // Act: Test 429 rate limit scenario
        var rateLimitStopwatch = Stopwatch.StartNew();
        try
        {
            var rateLimitRequest = new Request("https://ratelimit.com/test");
            var rateLimitResponse = await downloader.DownloadAsync(rateLimitRequest);
            rateLimitStopwatch.Stop();
            
            results.Add(("ratelimit.com", rateLimitResponse.StatusCode, true, 
                rateLimitHandler.AttemptCount, rateLimitStopwatch.Elapsed));
        }
        catch (Exception)
        {
            rateLimitStopwatch.Stop();
            results.Add(("ratelimit.com", HttpStatusCode.TooManyRequests, false, 
                rateLimitHandler.AttemptCount, rateLimitStopwatch.Elapsed));
        }

        // Act: Test 5xx server error scenario
        var serverErrorStopwatch = Stopwatch.StartNew();
        try
        {
            var serverErrorRequest = new Request("https://servererror.com/test");
            var serverErrorResponse = await downloader.DownloadAsync(serverErrorRequest);
            serverErrorStopwatch.Stop();
            
            results.Add(("servererror.com", serverErrorResponse.StatusCode, true, 
                serverErrorHandler.AttemptCount, serverErrorStopwatch.Elapsed));
        }
        catch (Exception)
        {
            serverErrorStopwatch.Stop();
            results.Add(("servererror.com", HttpStatusCode.InternalServerError, false, 
                serverErrorHandler.AttemptCount, serverErrorStopwatch.Elapsed));
        }

        // Act: Test 403 Forbidden error scenario
        var forbiddenStopwatch = Stopwatch.StartNew();
        try
        {
            var forbiddenRequest = new Request("https://forbidden.com/test");
            var forbiddenResponse = await downloader.DownloadAsync(forbiddenRequest);
            forbiddenStopwatch.Stop();
            
            results.Add(("forbidden.com", forbiddenResponse.StatusCode, true, 
                forbiddenHandler.AttemptCount, forbiddenStopwatch.Elapsed));
        }
        catch (Exception)
        {
            forbiddenStopwatch.Stop();
            results.Add(("forbidden.com", HttpStatusCode.Forbidden, false, 
                forbiddenHandler.AttemptCount, forbiddenStopwatch.Elapsed));
        }

        // Act: Test 404 Not Found error scenario
        var notFoundStopwatch = Stopwatch.StartNew();
        try
        {
            var notFoundRequest = new Request("https://notfound.com/test");
            var notFoundResponse = await downloader.DownloadAsync(notFoundRequest);
            notFoundStopwatch.Stop();
            
            results.Add(("notfound.com", notFoundResponse.StatusCode, true, 
                notFoundHandler.AttemptCount, notFoundStopwatch.Elapsed));
        }
        catch (Exception)
        {
            notFoundStopwatch.Stop();
            results.Add(("notfound.com", HttpStatusCode.NotFound, false, 
                notFoundHandler.AttemptCount, notFoundStopwatch.Elapsed));
        }

        // Verify individual handler retry counts
        // Retryable errors (timeout, 429, 5xx) should respect MaxRetryAttempts
        Assert.Equal(_options.MaxRetryAttempts + 1, timeoutHandler.AttemptCount);
        Assert.Equal(_options.MaxRetryAttempts + 1, rateLimitHandler.AttemptCount);
        Assert.Equal(_options.MaxRetryAttempts + 1, serverErrorHandler.AttemptCount);
        
        // Non-retryable errors (403, 404) should only be attempted once
        Assert.Equal(1, forbiddenHandler.AttemptCount);
        Assert.Equal(1, notFoundHandler.AttemptCount);
    }

    [Fact]
    public async Task E2E_ExponentialBackoffWithJitter_RespectsMaxRetryDelay()
    {
        // Arrange: Set up options with specific MaxRetryDelay for testing
        var backoffOptions = new AdaptiveThrottleOptions 
        { 
            EnableAdaptiveThrottling = true,
            MaxRetryAttempts = 10, // Allow more retries to test exponential growth
            MinConcurrency = 1,
            MaxConcurrency = 4,
            RequestSpacing = TimeSpan.FromMilliseconds(50),
            EwmaAlpha = 0.3,
            MinLatencyThresholdMs = 100,
            MaxLatencyThresholdMs = 1000,
            ErrorRateThreshold = 0.2,
            MaxRetryDelay = TimeSpan.FromSeconds(2) // Cap retry delay at 2 seconds
        };
        
        var backoffThrottleManager = new AdaptiveThrottleManager(backoffOptions);
        
        // Create a handler that tracks retry timestamps and always fails
        var backoffHandler = new ExponentialBackoffTrackingHandler("backoff.com");

        var httpClient = new HttpClient(backoffHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            new EmptyProxyService(),
            _loggerMock.Object,
            backoffThrottleManager,
            backoffOptions);

        // Act: Make request that will trigger retries with exponential backoff
        var overallStopwatch = Stopwatch.StartNew();
        try
        {
            var request = new Request("https://backoff.com/failing-endpoint");
            await downloader.DownloadAsync(request);
        }
        catch (Exception)
        {
            // Expected to fail after all retries
        }
        overallStopwatch.Stop();

        // Assert: Verify exponential backoff behavior
        var retryTimestamps = backoffHandler.GetRetryTimestamps();
        Assert.True(retryTimestamps.Count >= 2, "Should have at least 2 retry attempts");

        // Calculate delays between attempts
        var delays = new List<TimeSpan>();
        for (int i = 1; i < retryTimestamps.Count; i++)
        {
            delays.Add(retryTimestamps[i] - retryTimestamps[i - 1]);
        }
        
        // Check that we have meaningful delays (not all tiny delays)
        Assert.True(delays.Any(d => d.TotalMilliseconds > 100), 
            "At least one retry delay should be substantial (>100ms)");
        

        // Verify that at least one delay reached or approached MaxRetryDelay (showing capping works)
        var maxObservedDelay = delays.Max();
        Assert.True(maxObservedDelay <= backoffOptions.MaxRetryDelay + TimeSpan.FromMilliseconds(500), 
            $"Max observed delay ({maxObservedDelay.TotalMilliseconds}ms) should not exceed MaxRetryDelay ({backoffOptions.MaxRetryDelay.TotalMilliseconds}ms) by more than 500ms");
    }

    [Fact]
    public async Task E2E_SpiderWithAdaptiveDownloader_CrawlWebsite()
    {
        // Arrange: Create a mock website with varying latency patterns
        var mockSiteHandler = new MockedWebsiteHandler("testsite.com", new MockSiteConfig
        {
            LatencyMs = 150,
            ErrorRate = 0.1,
            StatusCode = HttpStatusCode.OK,
            SiteName = "Test E-commerce Site",
            ContentTemplate = "<html><head><title>Test Site - Page {PAGE}</title></head>" +
                            "<body><h1>Test E-commerce Site</h1>" +
                            "<div class='product'>Product {PAGE}</div>" +
                            "<p>Price: ${PAGE}0.00</p>" +
                            "<nav><a href='/page{NEXT}'>Next Page</a></nav>" +
                            "</body></html>"
        });

        var httpClient = new HttpClient(mockSiteHandler);
        
        // Create a test spider that uses adaptive downloader
        var testSpider = new TestAdaptiveSpider(httpClient, _options, _throttleManager);
        
        // Act: Run the spider for a limited time
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var crawlTask = testSpider.CrawlAsync(cts.Token);
        
        // Wait for crawl to complete or timeout
        try
        {
            await crawlTask;
        }
        catch (OperationCanceledException)
        {
            // Expected if we hit the timeout
        }

        // Assert: Verify that spider crawled pages and adaptive throttling was applied
        var results = testSpider.CrawledPages;
        Assert.True(results.Count > 0, "Spider should have crawled at least one page");
        Assert.True(results.Count <= 10, "Spider should respect throttling and not crawl too many pages");
        
        // Verify that pages contain expected content
        var firstResult = results.First();
        Assert.Contains("Test E-commerce Site", firstResult.Content);
        Assert.Contains("testsite.com", firstResult.Url);
        
        // Verify adaptive throttling was active
        var hostStats = testSpider.GetHostStatistics("testsite.com");
        Assert.NotNull(hostStats);
        Assert.True(hostStats.TotalRequests > 0, "Host statistics should show requests were made");
        Assert.True(hostStats.EwmaLatency > 0, "EWMA latency should be calculated");
    }

    public void Dispose()
    {
        _throttleManager?.Dispose();
        _globalCts?.Dispose();
    }
}

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

public class MockSiteConfig
{
    public int LatencyMs { get; set; } = 100;
    public double ErrorRate { get; set; } = 0.0;
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public string SiteName { get; set; } = "Mock Site";
    public string ContentTemplate { get; set; } = "<html><body><h1>Mock Page</h1></body></html>";
    public string ErrorContent { get; set; } = "<html><body><h1>Error</h1></body></html>";
}

public class MockedWebsiteHandler : HttpMessageHandler
{
    private readonly string _host;
    private readonly MockSiteConfig _config;
    private readonly Random _random = new();
    private int _requestCount;
    private readonly Dictionary<string, string> _pages = new();

    public MockedWebsiteHandler(string host, MockSiteConfig config)
    {
        _host = host;
        _config = config;
        
        // Pre-generate some realistic pages
        GeneratePages();
    }

    private void GeneratePages()
    {
        // Generate home page
        _pages["/"] = _config.ContentTemplate
            .Replace("{PAGE}", "Home")
            .Replace("{NEXT}", "1");
            
        // Generate numbered pages
        for (int i = 0; i < 20; i++)
        {
            var pageName = $"page{i}";
            var nextPage = i < 19 ? (i + 1).ToString() : "0";
            
            _pages[$"/{pageName}"] = _config.ContentTemplate
                .Replace("{PAGE}", pageName)
                .Replace("{NEXT}", nextPage);
        }
        
        // Generate some category pages
        var categories = new[] { "news", "sports", "tech", "business" };
        foreach (var category in categories)
        {
            _pages[$"/{category}"] = _config.ContentTemplate
                .Replace("{PAGE}", category.ToUpper())
                .Replace("{NEXT}", "1");
                
            for (int i = 1; i <= 5; i++)
            {
                _pages[$"/{category}/article{i}"] = _config.ContentTemplate
                    .Replace("{PAGE}", $"{category} Article {i}")
                    .Replace("{NEXT}", (i + 1).ToString());
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        
        // Simulate network latency with some variance
        var actualLatency = _config.LatencyMs + _random.Next(-_config.LatencyMs / 4, _config.LatencyMs / 4);
        await Task.Delay(Math.Max(10, actualLatency), cancellationToken);

        // Simulate error rate
        if (_random.NextDouble() < _config.ErrorRate)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(_config.ErrorContent, System.Text.Encoding.UTF8, "text/html"),
                Headers = { { "Server", $"{_config.SiteName}/1.0" } }
            };
        }

        // Get the requested path
        var path = request.RequestUri.PathAndQuery;
        if (path.Contains('?'))
        {
            path = path.Substring(0, path.IndexOf('?'));
        }

        // Find matching page content
        string content;
        if (_pages.TryGetValue(path, out var pageContent))
        {
            content = pageContent;
        }
        else
        {
            // Generate a default page for unknown paths
            content = _config.ContentTemplate
                .Replace("{PAGE}", $"Page: {path}")
                .Replace("{NEXT}", "1");
        }

        var response = new HttpResponseMessage(_config.StatusCode)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "text/html")
        };
        
        // Add realistic headers
        response.Headers.Add("Server", $"{_config.SiteName}/1.0");
        response.Headers.Add("X-Request-ID", Guid.NewGuid().ToString("N")[..8]);
        response.Headers.Add("X-Response-Time", $"{actualLatency}ms");
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromMinutes(5)
        };

        return response;
    }
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
            response.Headers.Add("Retry-After", "10"); // 10 seconds
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

public class RateLimitedApiHandler : HttpMessageHandler
{
    private readonly string _host;
    private int _requestCount;
    private DateTime _windowStart = DateTime.UtcNow;
    private readonly Dictionary<string, object> _apiData = new();

    public RateLimitedApiHandler(string host)
    {
        _host = host;
        InitializeApiData();
    }

    private void InitializeApiData()
    {
        // Generate some realistic API responses
        _apiData["/api/users"] = new { users = new[] { 
            new { id = 1, name = "John Doe", email = "john@example.com" },
            new { id = 2, name = "Jane Smith", email = "jane@example.com" }
        }};
        
        _apiData["/api/posts"] = new { posts = new[] {
            new { id = 1, title = "First Post", content = "Hello World" },
            new { id = 2, title = "Second Post", content = "More content" }
        }};
        
        _apiData["/api/status"] = new { status = "ok", version = "1.0", uptime = "99.9%" };
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
            var rateLimitResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    "{\"error\":\"Rate limit exceeded\",\"message\":\"Too many requests\"}",
                    System.Text.Encoding.UTF8, "application/json")
            };
            rateLimitResponse.Headers.Add("Retry-After", "1");
            rateLimitResponse.Headers.Add("X-RateLimit-Limit", "5");
            rateLimitResponse.Headers.Add("X-RateLimit-Remaining", "0");
            return rateLimitResponse;
        }

        // Get the requested path
        var path = request.RequestUri.PathAndQuery;
        if (path.Contains('?'))
        {
            path = path.Substring(0, path.IndexOf('?'));
        }

        // Return API data if available
        if (_apiData.TryGetValue(path, out var data))
        {
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(data);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.Add("X-API-Version", "1.0");
            response.Headers.Add("X-RateLimit-Limit", "5");
            response.Headers.Add("X-RateLimit-Remaining", (5 - _requestCount).ToString());
            return response;
        }

        // Default API response
        var defaultData = new { message = "API endpoint", path = path, timestamp = DateTime.UtcNow };
        var defaultJson = System.Text.Json.JsonSerializer.Serialize(defaultData);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(defaultJson, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// Test spider class that uses adaptive downloader for crawling
/// </summary>
public class TestAdaptiveSpider
{
    private readonly HttpClient _httpClient;
    private readonly AdaptiveThrottleOptions _options;
    private readonly AdaptiveThrottleManager _throttleManager;
    private readonly AdaptiveHttpClientDownloader _downloader;
    private readonly List<CrawledPage> _crawledPages;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<AdaptiveHttpClientDownloader>> _loggerMock;

    public List<CrawledPage> CrawledPages => _crawledPages;

    public TestAdaptiveSpider(HttpClient httpClient, AdaptiveThrottleOptions options, AdaptiveThrottleManager throttleManager)
    {
        _httpClient = httpClient;
        _options = options;
        _throttleManager = throttleManager;
        _crawledPages = new List<CrawledPage>();
        
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(_httpClient);
        
        _loggerMock = new Mock<ILogger<AdaptiveHttpClientDownloader>>();
        
        _downloader = new AdaptiveHttpClientDownloader(
            _httpClientFactoryMock.Object,
            new EmptyProxyService(),
            _loggerMock.Object,
            _throttleManager,
            _options);
    }

    public async Task CrawlAsync(CancellationToken cancellationToken = default)
    {
        var startUrls = new[]
        {
            "https://testsite.com/page1",
            "https://testsite.com/page2", 
            "https://testsite.com/page3"
        };

        var tasks = startUrls.Select(url => CrawlPageAsync(url, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task CrawlPageAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var request = new Request(url);
            var response = await _downloader.DownloadAsync(request);
            
            if (response != null && response.Content != null)
            {
                var content = response.ReadAsString();
                _crawledPages.Add(new CrawledPage
                {
                    Url = url,
                    Content = content,
                    StatusCode = response.StatusCode,
                    Success = true,
                    CrawledAt = DateTime.UtcNow
                });
                
                // Extract links for additional crawling (limited to prevent infinite crawl)
                if (_crawledPages.Count < 5) // Limit to prevent too many requests
                {
                    await ExtractAndCrawlLinks(content, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _crawledPages.Add(new CrawledPage
            {
                Url = url,
                Content = "",
                StatusCode = HttpStatusCode.InternalServerError,
                Success = false,
                Error = ex.Message,
                CrawledAt = DateTime.UtcNow
            });
        }
    }

    private async Task ExtractAndCrawlLinks(string content, CancellationToken cancellationToken)
    {
        // Simple link extraction - look for href="/pageN" patterns
        var linkMatches = System.Text.RegularExpressions.Regex.Matches(content, @"href=['""]([^'""]+)['""]");
        
        foreach (System.Text.RegularExpressions.Match match in linkMatches.Take(2)) // Limit links per page
        {
            if (cancellationToken.IsCancellationRequested) break;
            
            var href = match.Groups[1].Value;
            if (href.StartsWith("/page") && _crawledPages.Count < 10) // Additional safety limit
            {
                var fullUrl = $"https://testsite.com{href}";
                if (!_crawledPages.Any(p => p.Url == fullUrl))
                {
                    await CrawlPageAsync(fullUrl, cancellationToken);
                }
            }
        }
    }

    public HostGateStats GetHostStatistics(string host)
    {
        return _downloader.GetHostStats(host);
    }
}

/// <summary>
/// Represents a crawled page result
/// </summary>
public class CrawledPage
{
    public string Url { get; set; }
    public string Content { get; set; }
    public HttpStatusCode StatusCode { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; }
    public DateTime CrawledAt { get; set; }
}

/// <summary>
/// Message handler that simulates timeout errors with retry attempts
/// </summary>
public class TimeoutErrorMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private int _attemptCount;

    public int AttemptCount => _attemptCount;

    public TimeoutErrorMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attemptCount);

        // Simulate processing delay
        await Task.Delay(200, cancellationToken);

        // Always timeout to test retry behavior - the downloader should respect MaxRetryAttempts
        throw new TaskCanceledException("The operation was canceled due to timeout.", new TimeoutException());
    }
}

/// <summary>
/// Message handler that simulates 429 Too Many Requests errors with retry attempts
/// </summary>
public class RateLimitErrorMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private int _attemptCount;

    public int AttemptCount => _attemptCount;

    public RateLimitErrorMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attemptCount);

        // Simulate processing delay
        await Task.Delay(100, cancellationToken);

        // Always return 429 to test retry behavior
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent($"Rate limit exceeded on {_host} (attempt {_attemptCount})")
        };
        
        // Add Retry-After header for more realistic behavior
        response.Headers.Add("Retry-After", "2");
        
        return response;
    }
}

/// <summary>
/// Message handler that simulates 5xx server errors with retry attempts
/// </summary>
public class ServerErrorMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private int _attemptCount;
    private readonly Random _random = new();

    public int AttemptCount => _attemptCount;

    public ServerErrorMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attemptCount);

        // Simulate processing delay
        await Task.Delay(150, cancellationToken);

        // Randomly return different 5xx errors to test retry behavior
        var errorCodes = new[]
        {
            HttpStatusCode.InternalServerError,      // 500
            HttpStatusCode.BadGateway,              // 502
            HttpStatusCode.ServiceUnavailable,      // 503
            HttpStatusCode.GatewayTimeout          // 504
        };

        var errorCode = errorCodes[_random.Next(errorCodes.Length)];
        
        return new HttpResponseMessage(errorCode)
        {
            Content = new StringContent($"Server error {(int)errorCode} on {_host} (attempt {_attemptCount})")
        };
    }
}

/// <summary>
/// Message handler that tracks retry timestamps and always fails to test exponential backoff behavior
/// </summary>
public class ExponentialBackoffTrackingHandler : HttpMessageHandler
{
    private readonly string _host;
    private readonly List<DateTime> _retryTimestamps = new();
    private readonly Random _random = new();

    public ExponentialBackoffTrackingHandler(string host)
    {
        _host = host;
    }

    public List<DateTime> GetRetryTimestamps()
    {
        lock (_retryTimestamps)
        {
            return new List<DateTime>(_retryTimestamps);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Record timestamp of each attempt
        lock (_retryTimestamps)
        {
            _retryTimestamps.Add(DateTime.UtcNow);
        }

        // Simulate some processing time
        await Task.Delay(50, cancellationToken);

        // Always return a retryable error to test backoff behavior
        // Alternate between different 5xx errors to make it more realistic
        var errorCodes = new[]
        {
            HttpStatusCode.InternalServerError,   // 500
            HttpStatusCode.BadGateway,           // 502
            HttpStatusCode.ServiceUnavailable,   // 503
            HttpStatusCode.GatewayTimeout       // 504
        };

        var errorCode = errorCodes[_retryTimestamps.Count % errorCodes.Length];
        
        return new HttpResponseMessage(errorCode)
        {
            Content = new StringContent($"Simulated {(int)errorCode} error on {_host} (attempt {_retryTimestamps.Count})")
        };
    }
}

/// <summary>
/// Message handler that simulates 403 Forbidden errors with retry attempts
/// </summary>
public class ForbiddenErrorMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private int _attemptCount;

    public int AttemptCount => _attemptCount;

    public ForbiddenErrorMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attemptCount);

        // Simulate processing delay
        await Task.Delay(80, cancellationToken);

        // Always return 403 to test retry behavior
        return new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent($"Access forbidden on {_host} (attempt {_attemptCount})")
        };
    }
}

/// <summary>
/// Message handler that simulates 404 Not Found errors with retry attempts
/// </summary>
public class NotFoundErrorMessageHandler : HttpMessageHandler
{
    private readonly string _host;
    private int _attemptCount;

    public int AttemptCount => _attemptCount;

    public NotFoundErrorMessageHandler(string host)
    {
        _host = host;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attemptCount);

        // Simulate processing delay
        await Task.Delay(60, cancellationToken);

        // Always return 404 to test retry behavior
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"Resource not found on {_host} (attempt {_attemptCount})")
        };
    }
}
