using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

public interface IRequestRepository
{
    Task<RequestSearchResult> SearchAsync(RequestQueryParameters query, CancellationToken cancellationToken);

    Task<RequestDocument?> GetByIdAsync(ObjectId id, CancellationToken cancellationToken);
}

public sealed record RequestSearchResult(IReadOnlyList<RequestDocument> Items, long TotalCount);
