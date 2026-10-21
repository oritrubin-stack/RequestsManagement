using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

public interface IRequestSummaryCache
{
    Task<RequestSummaryDto> GetOrCreateAsync(
        Func<CancellationToken, Task<RequestSummaryDto>> factory,
        CancellationToken cancellationToken);

    // Call only after a write that affects the summary has succeeded.
    void Invalidate();
}

// In-process cache of the request summary. Each API instance has its own copy:
// Invalidate() affects only this instance, and other instances may serve a stale summary until TimeToLive expires.
public sealed class RequestSummaryCache : IRequestSummaryCache, IDisposable
{
    private const string CacheKey = "requests:summary";

    private readonly IMemoryCache _cache;
    private readonly TimeSpan _timeToLive;
    private readonly ILogger<RequestSummaryCache> _logger;

    // Single-flight: on a cache miss only one caller computes; the others wait and then read the cached value.
    private readonly SemaphoreSlim _computeLock = new(1, 1);

    // Guards the generation check together with Set, and the generation increment together with Remove,
    // so an invalidation cannot slip in between a computation's check and its store.
    private readonly object _storeLock = new();
    private long _generation;

    public RequestSummaryCache(
        IMemoryCache cache,
        IOptions<RequestSummaryCacheOptions> options,
        ILogger<RequestSummaryCache> logger)
    {
        _cache = cache;
        _timeToLive = options.Value.TimeToLive;
        _logger = logger;
    }

    public async Task<RequestSummaryDto> GetOrCreateAsync(
        Func<CancellationToken, Task<RequestSummaryDto>> factory,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey, out RequestSummaryDto? cached))
        {
            return cached!;
        }

        // A waiting caller that is cancelled only stops waiting; it does not affect the computation in progress.
        await _computeLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(CacheKey, out cached))
            {
                return cached!;
            }

            long generation;
            lock (_storeLock)
            {
                generation = _generation;
            }

            // A failure propagates to the caller and nothing is cached, so the next request retries.
            var summary = await factory(cancellationToken);

            lock (_storeLock)
            {
                if (generation == _generation)
                {
                    _cache.Set(CacheKey, summary, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = _timeToLive
                    });
                }
                else
                {
                    _logger.LogDebug("Request summary was invalidated while it was being computed; the result was not cached");
                }
            }

            return summary;
        }
        finally
        {
            _computeLock.Release();
        }
    }

    public void Invalidate()
    {
        lock (_storeLock)
        {
            _generation++;
            _cache.Remove(CacheKey);
        }
    }

    public void Dispose() => _computeLock.Dispose();
}
