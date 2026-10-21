using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

public interface IRequestRepository
{
    Task<RequestSearchResult> SearchAsync(RequestQueryParameters query, CancellationToken cancellationToken);

    Task<RequestDocument?> GetByIdAsync(ObjectId id, CancellationToken cancellationToken);

    Task<RequestSummaryCounts> GetSummaryAsync(CancellationToken cancellationToken);

    // Applies the status change only if the request still has the expected version.
    // Returns the updated request (without history), or null when no document matched.
    Task<RequestDocument?> TryUpdateStatusAsync(
        ObjectId id,
        int expectedVersion,
        StatusChangeEntry change,
        CancellationToken cancellationToken);

    // Returns null when the request does not exist, and an empty list when it has no history.
    Task<IReadOnlyList<StatusChangeEntry>?> GetHistoryAsync(ObjectId id, CancellationToken cancellationToken);
}

public sealed record RequestSearchResult(IReadOnlyList<RequestDocument> Items, long TotalCount);

// Counts only for values present in the collection; missing values are filled in by the service.
public sealed record RequestSummaryCounts(
    IReadOnlyDictionary<RequestStatus, long> ByStatus,
    IReadOnlyDictionary<RequestPriority, long> ByPriority);
