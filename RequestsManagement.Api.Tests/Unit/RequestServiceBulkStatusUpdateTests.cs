using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Tests.Unit;

public sealed class RequestServiceBulkStatusUpdateTests
{
    private const string ChangedBy = "dana.cohen";

    private readonly InMemoryRepository _repository = new();
    private readonly SpySummaryCache _cache = new();
    private readonly RequestService _service;

    public RequestServiceBulkStatusUpdateTests()
    {
        _service = new RequestService(_repository, _cache, TimeProvider.System, NullLogger<RequestService>.Instance);
    }

    [Fact]
    public async Task MixedBatch_ReturnsOutcomesInRequestOrder_ReadsOnce_AndInvalidatesOnce()
    {
        var updatable = _repository.Add(RequestStatus.New, version: 1);
        var missing = ObjectId.GenerateNewId();
        var stale = _repository.Add(RequestStatus.InProgress, version: 3);
        var final = _repository.Add(RequestStatus.Completed, version: 4);
        var secondUpdatable = _repository.Add(RequestStatus.Waiting, version: 2);

        var results = await _service.BulkUpdateStatusAsync(
        [
            new BulkStatusChange(updatable, RequestStatus.InProgress, 1),
            new BulkStatusChange(missing, RequestStatus.InProgress, 1),
            new BulkStatusChange(stale, RequestStatus.Waiting, 2),
            new BulkStatusChange(final, RequestStatus.InProgress, 4),
            new BulkStatusChange(secondUpdatable, RequestStatus.InProgress, 2)
        ], ChangedBy, CancellationToken.None);

        Assert.Equal(
            [updatable, missing, stale, final, secondUpdatable],
            results.Select(x => x.Change.Id));
        Assert.Equal(
            [StatusUpdateOutcome.Updated, StatusUpdateOutcome.NotFound, StatusUpdateOutcome.VersionConflict,
             StatusUpdateOutcome.InvalidTransition, StatusUpdateOutcome.Updated],
            results.Select(x => x.Result.Outcome));
        Assert.Equal(3, results[2].Result.CurrentVersion);
        Assert.Equal(RequestStatus.Completed, results[3].Result.CurrentStatus);

        Assert.Equal(1, _repository.GetByIdsCalls);
        Assert.Equal(0, _repository.GetByIdCalls);
        Assert.Equal([updatable, secondUpdatable], _repository.UpdateCalls.Select(x => x.Id));
        Assert.All(_repository.UpdateCalls, x => Assert.Equal(ChangedBy, x.Change.ChangedBy));
        Assert.Equal(1, _cache.Invalidations);
    }

    [Fact]
    public async Task BatchWithoutSuccessfulUpdate_DoesNotInvalidate()
    {
        var stale = _repository.Add(RequestStatus.New, version: 2);
        var final = _repository.Add(RequestStatus.Completed, version: 3);

        var results = await _service.BulkUpdateStatusAsync(
        [
            new BulkStatusChange(stale, RequestStatus.InProgress, 1),
            new BulkStatusChange(final, RequestStatus.InProgress, 3),
            new BulkStatusChange(ObjectId.GenerateNewId(), RequestStatus.InProgress, 1)
        ], ChangedBy, CancellationToken.None);

        Assert.DoesNotContain(results, x => x.Result.Outcome == StatusUpdateOutcome.Updated);
        Assert.Empty(_repository.UpdateCalls);
        Assert.Equal(0, _cache.Invalidations);
    }

    [Fact]
    public async Task ConcurrentChangeBetweenBatchReadAndWrite_IsAConflict_AndOtherItemsContinue()
    {
        var raced = _repository.Add(RequestStatus.New, version: 1);
        var other = _repository.Add(RequestStatus.New, version: 1);

        // Another update wins after the batch read but before this item's conditional update.
        _repository.BeforeUpdate = id =>
        {
            if (id == raced)
            {
                _repository.Bump(raced, RequestStatus.InProgress);
            }
        };

        var results = await _service.BulkUpdateStatusAsync(
        [
            new BulkStatusChange(raced, RequestStatus.InProgress, 1),
            new BulkStatusChange(other, RequestStatus.InProgress, 1)
        ], ChangedBy, CancellationToken.None);

        Assert.Equal(StatusUpdateOutcome.VersionConflict, results[0].Result.Outcome);
        Assert.Equal(2, results[0].Result.CurrentVersion);
        Assert.Equal(StatusUpdateOutcome.Updated, results[1].Result.Outcome);
        Assert.Equal(1, _cache.Invalidations);
    }

    [Fact]
    public async Task WriteFailureMidBatch_StopsProcessing_Propagates_AndInvalidatesOnce()
    {
        var first = _repository.Add(RequestStatus.New, version: 1);
        var failing = _repository.Add(RequestStatus.New, version: 1);
        var notAttempted = _repository.Add(RequestStatus.New, version: 1);
        _repository.BeforeUpdate = id =>
        {
            if (id == failing)
            {
                throw new TimeoutException("Simulated lost reply.");
            }
        };

        await Assert.ThrowsAsync<TimeoutException>(() => _service.BulkUpdateStatusAsync(
        [
            new BulkStatusChange(first, RequestStatus.InProgress, 1),
            new BulkStatusChange(failing, RequestStatus.InProgress, 1),
            new BulkStatusChange(notAttempted, RequestStatus.InProgress, 1)
        ], ChangedBy, CancellationToken.None));

        Assert.Equal([first, failing], _repository.UpdateCalls.Select(x => x.Id));
        Assert.Equal(2, _repository.Get(first).Version);
        Assert.Equal(1, _repository.Get(notAttempted).Version);
        Assert.Equal(1, _cache.Invalidations);
    }

    [Fact]
    public async Task WriteWithUnknownOutcomeOnFirstItem_InvalidatesConservatively()
    {
        var first = _repository.Add(RequestStatus.New, version: 1);
        _repository.BeforeUpdate = _ => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.BulkUpdateStatusAsync(
            [new BulkStatusChange(first, RequestStatus.InProgress, 1)], ChangedBy, CancellationToken.None));

        Assert.Equal(1, _cache.Invalidations);
    }

    [Fact]
    public async Task FailureBeforeAnyWrite_DoesNotInvalidate()
    {
        _repository.FailBatchRead = true;

        await Assert.ThrowsAsync<TimeoutException>(() => _service.BulkUpdateStatusAsync(
            [new BulkStatusChange(ObjectId.GenerateNewId(), RequestStatus.InProgress, 1)], ChangedBy, CancellationToken.None));

        Assert.Empty(_repository.UpdateCalls);
        Assert.Equal(0, _cache.Invalidations);
    }

    // A small in-memory repository whose conditional update matches only the expected version, like MongoDB.
    private sealed class InMemoryRepository : IRequestRepository
    {
        private readonly Dictionary<ObjectId, RequestDocument> _documents = [];

        public int GetByIdsCalls { get; private set; }

        public int GetByIdCalls { get; private set; }

        public bool FailBatchRead { get; set; }

        public Action<ObjectId>? BeforeUpdate { get; set; }

        public List<(ObjectId Id, int ExpectedVersion, StatusChangeEntry Change)> UpdateCalls { get; } = [];

        public ObjectId Add(RequestStatus status, int version)
        {
            var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var document = new RequestDocument
            {
                Id = ObjectId.GenerateNewId(),
                Title = "Bulk test request",
                OrganizationName = "Bulk Test Org",
                Status = status,
                Priority = RequestPriority.Low,
                AssignedTo = "bulk.test",
                CreatedAt = created,
                UpdatedAt = created,
                Version = version
            };
            _documents.Add(document.Id, document);
            return document.Id;
        }

        public RequestDocument Get(ObjectId id) => _documents[id];

        public void Bump(ObjectId id, RequestStatus status)
        {
            _documents[id].Status = status;
            _documents[id].Version++;
        }

        public Task<IReadOnlyList<RequestDocument>> GetByIdsAsync(IReadOnlyCollection<ObjectId> ids, CancellationToken cancellationToken)
        {
            GetByIdsCalls++;
            if (FailBatchRead)
            {
                throw new TimeoutException("Simulated unavailable database.");
            }

            IReadOnlyList<RequestDocument> found = ids.Where(_documents.ContainsKey).Select(id => Copy(_documents[id])).ToList();
            return Task.FromResult(found);
        }

        public Task<RequestDocument?> GetByIdAsync(ObjectId id, CancellationToken cancellationToken)
        {
            GetByIdCalls++;
            return Task.FromResult(_documents.TryGetValue(id, out var document) ? Copy(document) : null);
        }

        public Task<RequestDocument?> TryUpdateStatusAsync(
            ObjectId id,
            int expectedVersion,
            StatusChangeEntry change,
            CancellationToken cancellationToken)
        {
            UpdateCalls.Add((id, expectedVersion, change));
            BeforeUpdate?.Invoke(id);

            if (!_documents.TryGetValue(id, out var document) || document.Version != expectedVersion)
            {
                return Task.FromResult<RequestDocument?>(null);
            }

            document.Status = change.NewStatus;
            document.UpdatedAt = change.ChangedAt;
            document.Version++;
            document.StatusHistory.Add(change);
            return Task.FromResult<RequestDocument?>(Copy(document));
        }

        public Task<IReadOnlyList<StatusChangeEntry>?> GetHistoryAsync(ObjectId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RequestSearchResult> SearchAsync(RequestQueryParameters query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RequestSummaryCounts> GetSummaryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static RequestDocument Copy(RequestDocument source) =>
            new()
            {
                Id = source.Id,
                Title = source.Title,
                OrganizationName = source.OrganizationName,
                Status = source.Status,
                Priority = source.Priority,
                AssignedTo = source.AssignedTo,
                CreatedAt = source.CreatedAt,
                UpdatedAt = source.UpdatedAt,
                Version = source.Version
            };
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
}
