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
            // arrives (the driver does not stop the command on the server). The outcome is unknown, so the summary
            // is invalidated conservatively; the exception still propagates and no success is reported.
            _summaryCache.Invalidate();
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

        // Only after the write has succeeded.
        _summaryCache.Invalidate();

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
