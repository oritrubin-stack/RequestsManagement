using System.Diagnostics;
using MongoDB.Bson;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

public sealed class RequestService
{
    private readonly IRequestRepository _repository;
    private readonly ILogger<RequestService> _logger;

    public RequestService(IRequestRepository repository, ILogger<RequestService> logger)
    {
        _repository = repository;
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
