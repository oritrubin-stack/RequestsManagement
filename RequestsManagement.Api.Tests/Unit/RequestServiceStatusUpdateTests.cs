using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Tests.Unit;

public sealed class RequestServiceStatusUpdateTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, 123, DateTimeKind.Utc);

    private readonly FakeRepository _repository = new();
    private readonly SpySummaryCache _cache = new();
    private readonly FixedTimeProvider _time = new(Now);
    private readonly RequestService _service;

    public RequestServiceStatusUpdateTests()
    {
        _service = new RequestService(_repository, _cache, _time, NullLogger<RequestService>.Instance);
    }

    [Fact]
    public async Task ValidUpdate_WritesHistoryEntryAndInvalidatesSummary()
    {
        var current = CreateRequest(RequestStatus.New, version: 1);
        _repository.Reads.Enqueue(current);
        _repository.UpdateResult = (_, _, change) => Updated(current, change);

        var result = await _service.UpdateStatusAsync(current.Id, RequestStatus.InProgress, 1, "dana.cohen", CancellationToken.None);

        Assert.Equal(StatusUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(2, result.Request!.Version);
        Assert.Equal(RequestStatus.InProgress, result.Request.Status);

        var (id, expectedVersion, change) = Assert.Single(_repository.UpdateCalls);
        Assert.Equal(current.Id, id);
        Assert.Equal(1, expectedVersion);
        Assert.Equal(RequestStatus.New, change.PreviousStatus);
        Assert.Equal(RequestStatus.InProgress, change.NewStatus);
        Assert.Equal("dana.cohen", change.ChangedBy);
        Assert.Equal(Now, change.ChangedAt);
        Assert.Equal(1, _cache.Invalidations);
    }

    [Fact]
    public async Task MissingRequest_ReturnsNotFound_WithoutWriteOrInvalidation()
    {
        _repository.Reads.Enqueue(null);

        var result = await _service.UpdateStatusAsync(ObjectId.GenerateNewId(), RequestStatus.InProgress, 1, "dana.cohen", CancellationToken.None);

        Assert.Equal(StatusUpdateOutcome.NotFound, result.Outcome);
        Assert.Empty(_repository.UpdateCalls);
        Assert.Equal(0, _cache.Invalidations);
    }

    [Fact]
    public async Task StaleVersion_ReturnsConflictWithCurrentState_WithoutWriteOrInvalidation()
    {
        _repository.Reads.Enqueue(CreateRequest(RequestStatus.InProgress, version: 4));

        var result = await _service.UpdateStatusAsync(ObjectId.GenerateNewId(), RequestStatus.Waiting, 3, "dana.cohen", CancellationToken.None);

        Assert.Equal(StatusUpdateOutcome.VersionConflict, result.Outcome);
        Assert.Equal(4, result.CurrentVersion);
        Assert.Equal(RequestStatus.InProgress, result.CurrentStatus);
        Assert.Empty(_repository.UpdateCalls);
        Assert.Equal(0, _cache.Invalidations);
    }

    [Theory]
    [InlineData(RequestStatus.Completed, RequestStatus.InProgress)]
    [InlineData(RequestStatus.New, RequestStatus.Completed)]
    [InlineData(RequestStatus.InProgress, RequestStatus.InProgress)]
    public async Task DisallowedTransition_ReturnsInvalidTransition_WithoutWriteOrInvalidation(RequestStatus from, RequestStatus to)
    {
        _repository.Reads.Enqueue(CreateRequest(from, version: 2));

        var result = await _service.UpdateStatusAsync(ObjectId.GenerateNewId(), to, 2, "dana.cohen", CancellationToken.None);

        Assert.Equal(StatusUpdateOutcome.InvalidTransition, result.Outcome);
        Assert.Equal(from, result.CurrentStatus);
        Assert.Empty(_repository.UpdateCalls);
        Assert.Equal(0, _cache.Invalidations);
    }

    [Fact]
    public async Task ConcurrentUpdateBetweenReadAndWrite_ReturnsConflictWithLatestVersion()
    {
        // The read sees version 1, but another update wins before this one is written.
        var current = CreateRequest(RequestStatus.New, version: 1);
        _repository.Reads.Enqueue(current);
        _repository.Reads.Enqueue(CreateRequest(RequestStatus.InProgress, version: 2, id: current.Id));
        _repository.UpdateResult = (_, _, _) => null;

        var result = await _service.UpdateStatusAsync(current.Id, RequestStatus.InProgress, 1, "dana.cohen", CancellationToken.None);

        Assert.Equal(StatusUpdateOutcome.VersionConflict, result.Outcome);
        Assert.Equal(2, result.CurrentVersion);
        Assert.Equal(RequestStatus.InProgress, result.CurrentStatus);
        Assert.Single(_repository.UpdateCalls);
        Assert.Equal(0, _cache.Invalidations);
    }

    [Fact]
    public async Task RequestRemovedBetweenReadAndWrite_ReturnsNotFound()
    {
        var current = CreateRequest(RequestStatus.New, version: 1);
        _repository.Reads.Enqueue(current);
        _repository.Reads.Enqueue(null);
        _repository.UpdateResult = (_, _, _) => null;

        var result = await _service.UpdateStatusAsync(current.Id, RequestStatus.InProgress, 1, "dana.cohen", CancellationToken.None);

        Assert.Equal(StatusUpdateOutcome.NotFound, result.Outcome);
        Assert.Equal(0, _cache.Invalidations);
    }

    [Fact]
    public async Task WriteCancelledAfterBeingSent_PropagatesAndInvalidatesConservatively()
    {
        var current = CreateRequest(RequestStatus.New, version: 1);
        _repository.Reads.Enqueue(current);
        _repository.UpdateResult = (_, _, _) => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.UpdateStatusAsync(current.Id, RequestStatus.InProgress, 1, "dana.cohen", CancellationToken.None));

        // The update may have been applied on the server, so the summary must not stay cached.
        Assert.Equal(1, _cache.Invalidations);
    }

    [Fact]
    public async Task WriteWithUnknownOutcome_PropagatesAndInvalidatesConservatively()
    {
        var current = CreateRequest(RequestStatus.New, version: 1);
        _repository.Reads.Enqueue(current);
        _repository.UpdateResult = (_, _, _) => throw new TimeoutException("Simulated lost reply.");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            _service.UpdateStatusAsync(current.Id, RequestStatus.InProgress, 1, "dana.cohen", CancellationToken.None));

        Assert.Equal(1, _cache.Invalidations);
    }

    [Fact]
    public async Task ClockBehindLastUpdate_KeepsTimestampsInOrder()
    {
        var lastUpdate = Now.AddMinutes(5);
        var current = CreateRequest(RequestStatus.New, version: 1, updatedAt: lastUpdate);
        _repository.Reads.Enqueue(current);
        _repository.UpdateResult = (_, _, change) => Updated(current, change);

        await _service.UpdateStatusAsync(current.Id, RequestStatus.InProgress, 1, "dana.cohen", CancellationToken.None);

        Assert.Equal(lastUpdate, Assert.Single(_repository.UpdateCalls).Change.ChangedAt);
    }

    [Fact]
    public async Task Timestamp_IsTruncatedToMilliseconds()
    {
        _time.Now = new DateTimeOffset(Now.AddTicks(5_678));
        var current = CreateRequest(RequestStatus.New, version: 1);
        _repository.Reads.Enqueue(current);
        _repository.UpdateResult = (_, _, change) => Updated(current, change);

        await _service.UpdateStatusAsync(current.Id, RequestStatus.InProgress, 1, "dana.cohen", CancellationToken.None);

        Assert.Equal(Now, Assert.Single(_repository.UpdateCalls).Change.ChangedAt);
    }

    [Fact]
    public async Task History_ForMissingRequest_IsNull_AndEntriesCarryTheRequestId()
    {
        var id = ObjectId.GenerateNewId();
        _repository.History = null;
        Assert.Null(await _service.GetHistoryAsync(id, CancellationToken.None));

        _repository.History =
        [
            new StatusChangeEntry { PreviousStatus = RequestStatus.New, NewStatus = RequestStatus.InProgress, ChangedAt = Now, ChangedBy = "dana.cohen" }
        ];
        var history = await _service.GetHistoryAsync(id, CancellationToken.None);

        var entry = Assert.Single(history!);
        Assert.Equal(id.ToString(), entry.RequestId);
        Assert.Equal(RequestStatus.New, entry.PreviousStatus);
        Assert.Equal("dana.cohen", entry.ChangedBy);
    }

    private static RequestDocument CreateRequest(RequestStatus status, int version, DateTime? updatedAt = null, ObjectId? id = null) =>
        new()
        {
            Id = id ?? ObjectId.GenerateNewId(),
            Title = "Test request",
            OrganizationName = "Test Org",
            Status = status,
            Priority = RequestPriority.Medium,
            AssignedTo = "test.user",
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = updatedAt ?? Now.AddDays(-1),
            Version = version
        };

    private static RequestDocument Updated(RequestDocument current, StatusChangeEntry change) =>
        CreateRequest(change.NewStatus, current.Version + 1, change.ChangedAt, current.Id);

    private sealed class FakeRepository : IRequestRepository
    {
        public Queue<RequestDocument?> Reads { get; } = new();

        public Func<ObjectId, int, StatusChangeEntry, RequestDocument?> UpdateResult { get; set; } = (_, _, _) => null;

        public List<(ObjectId Id, int ExpectedVersion, StatusChangeEntry Change)> UpdateCalls { get; } = [];

        public IReadOnlyList<StatusChangeEntry>? History { get; set; }

        public Task<RequestDocument?> GetByIdAsync(ObjectId id, CancellationToken cancellationToken) =>
            Task.FromResult(Reads.Dequeue());

        public Task<IReadOnlyList<RequestDocument>> GetByIdsAsync(IReadOnlyCollection<ObjectId> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RequestDocument?> TryUpdateStatusAsync(
            ObjectId id,
            int expectedVersion,
            StatusChangeEntry change,
            CancellationToken cancellationToken)
        {
            UpdateCalls.Add((id, expectedVersion, change));
            return Task.FromResult(UpdateResult(id, expectedVersion, change));
        }

        public Task<IReadOnlyList<StatusChangeEntry>?> GetHistoryAsync(ObjectId id, CancellationToken cancellationToken) =>
            Task.FromResult(History);

        public Task<RequestSearchResult> SearchAsync(RequestQueryParameters query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RequestSummaryCounts> GetSummaryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SpySummaryCache : IRequestSummaryCache
    {
        public int Invalidations { get; private set; }

        public Task<RequestSummaryDto> GetOrCreateAsync(
            Func<CancellationToken, Task<RequestSummaryDto>> factory,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Invalidate() => Invalidations++;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public FixedTimeProvider(DateTime now)
        {
            Now = new DateTimeOffset(now);
        }

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
