using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DotnetSpider.Robots;

public interface ICrawlDelayManager
{
    Task ApplyCrawlDelayAsync(string host, TimeSpan crawlDelay, CancellationToken cancellationToken = default);
}

public class CrawlDelayManager : ICrawlDelayManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRequestTimes = new();
    private readonly ILogger<CrawlDelayManager> _logger;

    public CrawlDelayManager(ILogger<CrawlDelayManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ApplyCrawlDelayAsync(string host, TimeSpan crawlDelay, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(host) || crawlDelay <= TimeSpan.Zero)
            return;

        var semaphore = _hostSemaphores.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            
            if (_lastRequestTimes.TryGetValue(host, out var lastRequestTime))
            {
                var timeSinceLastRequest = now - lastRequestTime;
                var remainingDelay = crawlDelay - timeSinceLastRequest;
                
                if (remainingDelay > TimeSpan.Zero)
                {
                    _logger.LogDebug("Applying crawl-delay of {DelayMs}ms for host {Host}", 
                        remainingDelay.TotalMilliseconds, host);
                    await Task.Delay(remainingDelay, cancellationToken);
                }
            }
            
            _lastRequestTimes.AddOrUpdate(host, DateTime.UtcNow, (_, _) => DateTime.UtcNow);
        }
        finally
        {
            semaphore.Release();
        }
    }
}