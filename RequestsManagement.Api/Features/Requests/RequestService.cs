using System.Diagnostics;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

public sealed class RequestService
{
    private readonly IRequestRepository _repository;
    private readonly IRequestSummaryCache _summaryCache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RequestService> _logger;

    public RequestService(
        IRequestRepository repository,
        IRequestSummaryCache summaryCache,
        TimeProvider timeProvider,
        ILogger<RequestService> logger)
    {
        _repository = repository;
        _summaryCache = summaryCache;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<PagedResult<RequestDto>> SearchAsync(RequestQueryParameters query, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await _repository.SearchAsync(query, cancellationToken);

        // Only the names of the active filters are logged; search text and filter values are not.
        _logger.LogDebug(
            "Searched requests with filters [{Filters}], sort {SortBy} {SortDirection}, page {Page}: {Returned} of {Total} in {ElapsedMs} ms",
            string.Join(", ", GetActiveFilterNames(query)),
            query.SortBy,
            query.SortDirection,
            query.Page,
            result.Items.Count,
            result.TotalCount,
            stopwatch.ElapsedMilliseconds);

        var items = result.Items.Select(document => document.ToDto()).ToList();
        return PagedResult<RequestDto>.Create(items, query.Page, query.PageSize, result.TotalCount);
    }

    public async Task<RequestDto?> GetByIdAsync(ObjectId id, CancellationToken cancellationToken)
    {
        var document = await _repository.GetByIdAsync(id, cancellationToken);
        return document?.ToDto();
    }

    public async Task<StatusUpdateResult> UpdateStatusAsync(
        ObjectId id,
        RequestStatus newStatus,
        int expectedVersion,
        string changedBy,
        CancellationToken cancellationToken)
    {
        var current = await _repository.GetByIdAsync(id, cancellationToken);
        if (current is null)
        {
            return StatusUpdateResult.NotFound();
        }

        var writes = new WriteTracker();
        try
        {
            return await ApplyStatusChangeAsync(current, newStatus, expectedVersion, changedBy, writes, cancellationToken);
        }
        finally
        {
            // After a successful write, or when a write may have been applied (unknown outcome).
            if (writes.PossiblyWritten)
            {
                _summaryCache.Invalidate();
            }
        }
    }

    // Partial success: each change is validated and applied on its own, in request order, with the same rules and the
    // same conditional update as a single update. Processing stops at the first exception (for example, the database
    // is unavailable); changes applied before it stay applied, and the exception propagates.
    public async Task<IReadOnlyList<BulkStatusChangeResult>> BulkUpdateStatusAsync(
        IReadOnlyList<BulkStatusChange> changes,
        string changedBy,
        CancellationToken cancellationToken)
    {
        // One read for the whole batch (only the requested ids). It feeds validation and the history entries;
        // whether each change applies is still decided by its own conditional update on _id and version.
        var existing = (await _repository.GetByIdsAsync(changes.Select(x => x.Id).ToList(), cancellationToken))
            .ToDictionary(x => x.Id);

        var results = new List<BulkStatusChangeResult>(changes.Count);
        var writes = new WriteTracker();
        try
        {
            foreach (var change in changes)
            {
                var result = existing.TryGetValue(change.Id, out var current)
                    ? await ApplyStatusChangeAsync(current, change.NewStatus, change.ExpectedVersion, changedBy, writes, cancellationToken)
                    : StatusUpdateResult.NotFound();

                results.Add(new BulkStatusChangeResult(change, result));
            }
        }
        finally
        {
            // Once per batch: after at least one successful write, or a write with an unknown outcome,
            // including when the batch stops early because of an exception.
            if (writes.PossiblyWritten)
            {
                _summaryCache.Invalidate();
            }
        }

        _logger.LogInformation(
            "Bulk status update of {Count} requests: {Updated} updated, {NotFound} not found, {Conflicts} version conflicts, {InvalidTransitions} invalid transitions",
            results.Count,
            results.Count(x => x.Result.Outcome == StatusUpdateOutcome.Updated),
            results.Count(x => x.Result.Outcome == StatusUpdateOutcome.NotFound),
            results.Count(x => x.Result.Outcome == StatusUpdateOutcome.VersionConflict),
            results.Count(x => x.Result.Outcome == StatusUpdateOutcome.InvalidTransition));

        return results;
    }

    // Validates and applies one status change to a request that was read beforehand.
    // The read only feeds validation and the history entry; the conditional update decides whether the change applies.
    // The caller invalidates the summary cache when the tracker reports a possible write.
    private async Task<StatusUpdateResult> ApplyStatusChangeAsync(
        RequestDocument current,
        RequestStatus newStatus,
        int expectedVersion,
        string changedBy,
        WriteTracker writes,
        CancellationToken cancellationToken)
    {
        var id = current.Id;

        // Checked before the transition: a client holding a stale version must refresh,
        // because judging the transition against a state it has not seen would be meaningless.
        if (current.Version != expectedVersion)
        {
            _logger.LogInformation(
                "Status update of request {RequestId} rejected: expected version {ExpectedVersion}, current version {CurrentVersion}",
                id,
                expectedVersion,
                current.Version);
            return StatusUpdateResult.VersionConflict(current.Version, current.Status);
        }

        if (!StatusTransitions.IsAllowed(current.Status, newStatus))
        {
            return StatusUpdateResult.InvalidTransition(current.Version, current.Status);
        }

        // PreviousStatus comes from the read above. It is still accurate when the update matches,
        // because any status change would also have changed the version that the update filters on.
        var change = new StatusChangeEntry
        {
            PreviousStatus = current.Status,
            NewStatus = newStatus,
            ChangedAt = NextTimestamp(current.UpdatedAt),
            ChangedBy = changedBy
        };

        // Correctness relies on this conditional update, not on the read above.
        RequestDocument? updated;
        try
        {
            updated = await _repository.TryUpdateStatusAsync(id, expectedVersion, change, cancellationToken);
        }
        catch
        {
            // Once sent, the update may have been applied even if the call is cancelled or fails before the reply
            // arrives (the driver does not stop the command on the server). The outcome is unknown, so it is reported
            // as a possible write (the summary is invalidated conservatively); the exception still propagates.
            writes.PossiblyWritten = true;
            throw;
        }

        if (updated is null)
        {
            // Another write changed (or removed) the request between the read and the update.
            var latest = await _repository.GetByIdAsync(id, cancellationToken);
            if (latest is null)
            {
                return StatusUpdateResult.NotFound();
            }

            _logger.LogInformation(
                "Status update of request {RequestId} lost a concurrent update: expected version {ExpectedVersion}, current version {CurrentVersion}",
                id,
                expectedVersion,
                latest.Version);
            return StatusUpdateResult.VersionConflict(latest.Version, latest.Status);
        }

        writes.PossiblyWritten = true;

        _logger.LogInformation(
            "Request {RequestId} status changed from {PreviousStatus} to {NewStatus}, version {Version}",
            id,
            change.PreviousStatus,
            change.NewStatus,
            updated.Version);

        return StatusUpdateResult.Updated(updated.ToDto());
    }

    public async Task<IReadOnlyList<StatusChangeDto>?> GetHistoryAsync(ObjectId id, CancellationToken cancellationToken)
    {
        var history = await _repository.GetHistoryAsync(id, cancellationToken);
        return history?.Select(entry => entry.ToDto(id)).ToList();
    }

    public Task<RequestSummaryDto> GetSummaryAsync(CancellationToken cancellationToken) =>
        _summaryCache.GetOrCreateAsync(ComputeSummaryAsync, cancellationToken);

    private async Task<RequestSummaryDto> ComputeSummaryAsync(CancellationToken cancellationToken)
    {
        // Taken before the query, so GeneratedAt never claims the data is fresher than it is.
        var generatedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var counts = await _repository.GetSummaryAsync(cancellationToken);
        var summary = BuildSummary(counts, generatedAt);

        _logger.LogDebug(
            "Computed request summary of {TotalCount} requests in {ElapsedMs} ms",
            summary.TotalCount,
            stopwatch.ElapsedMilliseconds);

        return summary;
    }

    // Fills in every enum value (zero when absent) so clients always receive the same shape.
    private static RequestSummaryDto BuildSummary(RequestSummaryCounts counts, DateTime generatedAt)
    {
        var byStatus = Enum.GetValues<RequestStatus>()
            .Select(status => new StatusCountDto(status, counts.ByStatus.GetValueOrDefault(status)))
            .ToList();

        var byPriority = Enum.GetValues<RequestPriority>()
            .Select(priority => new PriorityCountDto(priority, counts.ByPriority.GetValueOrDefault(priority)))
            .ToList();

        return new RequestSummaryDto(byStatus.Sum(x => x.Count), byStatus, byPriority, generatedAt);
    }

    // Millisecond precision matches what MongoDB stores. Never earlier than the previous update,
    // so the history stays in order even if server clocks differ between API instances.
    private DateTime NextTimestamp(DateTime previousUpdate)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var truncated = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        return truncated > previousUpdate ? truncated : DateTime.SpecifyKind(previousUpdate, DateTimeKind.Utc);
    }

    // Set (never cleared) when a write succeeded or may have succeeded, so an operation invalidates the summary once.
    private sealed class WriteTracker
    {
        public bool PossiblyWritten { get; set; }
    }

    private static IEnumerable<string> GetActiveFilterNames(RequestQueryParameters query)
    {
        if (!string.IsNullOrWhiteSpace(query.Search)) yield return nameof(query.Search);
        if (query.Status.HasValue) yield return nameof(query.Status);
        if (query.Priority.HasValue) yield return nameof(query.Priority);
        if (!string.IsNullOrEmpty(query.OrganizationName)) yield return nameof(query.OrganizationName);
        if (!string.IsNullOrEmpty(query.AssignedTo)) yield return nameof(query.AssignedTo);
        if (query.CreatedFrom.HasValue) yield return nameof(query.CreatedFrom);
        if (query.CreatedTo.HasValue) yield return nameof(query.CreatedTo);
    }
}
