using System.Diagnostics;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

public sealed class RequestService
{
    private readonly IRequestRepository _repository;
    private readonly IRequestSummaryCache _summaryCache;
    private readonly ILogger<RequestService> _logger;

    public RequestService(IRequestRepository repository, IRequestSummaryCache summaryCache, ILogger<RequestService> logger)
    {
        _repository = repository;
        _summaryCache = summaryCache;
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
