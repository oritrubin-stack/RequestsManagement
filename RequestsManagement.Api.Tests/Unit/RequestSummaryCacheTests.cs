using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RequestsManagement.Api.Features.Requests;
using RequestsManagement.Api.Features.Requests.Dtos;
using RequestsManagement.Api.Tests.Infrastructure;

namespace RequestsManagement.Api.Tests.Unit;

public sealed class RequestSummaryCacheTests : IDisposable
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    private readonly ManualSystemClock _clock = new();
    private readonly MemoryCache _memoryCache;
    private readonly RequestSummaryCache _cache;
    private int _factoryCalls;

    public RequestSummaryCacheTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
        _cache = new RequestSummaryCache(
            _memoryCache,
            Options.Create(new RequestSummaryCacheOptions { TimeToLive = TimeToLive }),
            NullLogger<RequestSummaryCache>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _memoryCache.Dispose();
    }

    [Fact]
    public async Task SecondCall_ReturnsCachedSummaryWithoutRecomputing()
    {
        var first = await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);
        var second = await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        Assert.Equal(1, _factoryCalls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task JustBeforeTimeToLive_SummaryIsStillCached()
    {
        await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        _clock.Advance(TimeToLive - TimeSpan.FromMilliseconds(1));
        await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public async Task AfterTimeToLive_SummaryIsRecomputed()
    {
        await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        _clock.Advance(TimeToLive + TimeSpan.FromMilliseconds(1));
        var refreshed = await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        Assert.Equal(2, _factoryCalls);
        Assert.Equal(2, refreshed.TotalCount);
    }

    [Fact]
    public async Task Invalidate_ForcesRecomputation()
    {
        await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        _cache.Invalidate();
        var refreshed = await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        Assert.Equal(2, _factoryCalls);
        Assert.Equal(2, refreshed.TotalCount);
    }

    [Fact]
    public async Task FailedComputation_IsNotCached_AndNextCallRetries()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            _cache.GetOrCreateAsync(_ => throw new TimeoutException("Simulated database timeout."), CancellationToken.None));

        var summary = await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);
        await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        Assert.Equal(1, _factoryCalls);
        Assert.Equal(1, summary.TotalCount);
    }

    [Fact]
    public async Task ConcurrentCallsOnMiss_ComputeOnlyOnce()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RequestSummaryDto> BlockingFactory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _factoryCalls);
            started.TrySetResult();
            await release.Task;
            return CreateSummary(_factoryCalls);
        }

        var first = _cache.GetOrCreateAsync(BlockingFactory, CancellationToken.None);
        await started.Task;

        // Called directly (not via Task.Run): each call runs synchronously until it blocks and returns a pending task,
        // so all 20 calls are guaranteed to be in flight while the first computation is still running.
        var others = Enumerable.Range(0, 20)
            .Select(_ => _cache.GetOrCreateAsync(BlockingFactory, CancellationToken.None))
            .ToList();

        Assert.False(first.IsCompleted);
        Assert.All(others, call => Assert.False(call.IsCompleted));
        Assert.Equal(1, _factoryCalls);

        release.SetResult();
        var results = await Task.WhenAll(others.Append(first));

        Assert.Equal(1, _factoryCalls);
        Assert.All(results, result => Assert.Same(results[0], result));
    }

    [Fact]
    public async Task InvalidateDuringComputation_ReturnsResultButDoesNotCacheIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RequestSummaryDto> BlockingFactory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _factoryCalls);
            started.TrySetResult();
            await release.Task;
            return CreateSummary(_factoryCalls);
        }

        var computation = _cache.GetOrCreateAsync(BlockingFactory, CancellationToken.None);
        await started.Task;

        // A write completes and invalidates while the (now stale) computation is still running.
        _cache.Invalidate();
        release.SetResult();
        var staleResult = await computation;

        var next = await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        Assert.Equal(1, staleResult.TotalCount);
        Assert.Equal(2, _factoryCalls);
        Assert.Equal(2, next.TotalCount);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelComputationForOtherCallers()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RequestSummaryDto> BlockingFactory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _factoryCalls);
            started.TrySetResult();
            await release.Task;
            return CreateSummary(_factoryCalls);
        }

        var computing = _cache.GetOrCreateAsync(BlockingFactory, CancellationToken.None);
        await started.Task;

        using var waiterCancellation = new CancellationTokenSource();
        var waiter = _cache.GetOrCreateAsync(BlockingFactory, waiterCancellation.Token);
        Assert.False(waiter.IsCompleted);

        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(computing.IsCompleted);

        release.SetResult();
        var computed = await computing;
        var later = await _cache.GetOrCreateAsync(BlockingFactory, CancellationToken.None);

        Assert.Equal(1, _factoryCalls);
        Assert.Same(computed, later);
    }

    [Fact]
    public async Task CancelledComputation_IsNotCached_AndNextWaiterComputes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RequestSummaryDto> CancellableFactory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _factoryCalls);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return CreateSummary(_factoryCalls);
        }

        using var computingCancellation = new CancellationTokenSource();
        var computing = _cache.GetOrCreateAsync(CancellableFactory, computingCancellation.Token);
        await started.Task;

        var waiter = _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        computingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => computing);

        var computedByWaiter = await waiter;
        var cached = await _cache.GetOrCreateAsync(CountingFactory, CancellationToken.None);

        Assert.Equal(2, _factoryCalls);
        Assert.Equal(2, computedByWaiter.TotalCount);
        Assert.Same(computedByWaiter, cached);
    }

    private Task<RequestSummaryDto> CountingFactory(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _factoryCalls);
        return Task.FromResult(CreateSummary(call));
    }

    // TotalCount carries the call number so tests can tell which computation produced a result.
    private static RequestSummaryDto CreateSummary(long totalCount) =>
        new(totalCount, [], [], DateTime.UtcNow);
}
