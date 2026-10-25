using MongoDB.Bson;
using MongoDB.Driver;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;
using SortDirection = RequestsManagement.Api.Features.Requests.Dtos.SortDirection;

namespace RequestsManagement.Api.Features.Requests;

// Filtering, sorting, paging and counting are all executed by MongoDB; only one page of documents,
// without their status history, is transferred to the application.
public sealed class RequestRepository : IRequestRepository
{
    private static readonly FilterDefinitionBuilder<RequestDocument> Filter = Builders<RequestDocument>.Filter;
    private static readonly SortDefinitionBuilder<RequestDocument> Sort = Builders<RequestDocument>.Sort;

    private static readonly ProjectionDefinition<RequestDocument> WithoutHistory =
        Builders<RequestDocument>.Projection.Exclude(x => x.StatusHistory);

    private const string ByStatusFacet = "byStatus";
    private const string ByPriorityFacet = "byPriority";

    private readonly IMongoCollection<RequestDocument> _collection;

    public RequestRepository(IMongoCollection<RequestDocument> collection)
    {
        _collection = collection;
    }

    public async Task<RequestSearchResult> SearchAsync(RequestQueryParameters query, CancellationToken cancellationToken)
    {
        var filters = BuildFilters(query);
        var filter = filters.Count == 0 ? Filter.Empty : Filter.And(filters);

        var itemsTask = _collection
            .Find(filter)
            .Project<RequestDocument>(WithoutHistory)
            .Sort(BuildSort(query.SortBy, query.SortDirection))
            .Skip((query.Page - 1) * query.PageSize)
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);

        // An unfiltered countDocuments scans the whole collection (measured ~200 ms on 100K documents),
        // while the estimated count reads collection metadata.
        var countTask = filters.Count == 0
            ? _collection.EstimatedDocumentCountAsync(cancellationToken: cancellationToken)
            : _collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        await Task.WhenAll(itemsTask, countTask);

        return new RequestSearchResult(itemsTask.Result, countTask.Result);
    }

    public async Task<RequestDocument?> GetByIdAsync(ObjectId id, CancellationToken cancellationToken) =>
        await _collection
            .Find(Filter.Eq(x => x.Id, id))
            .Project<RequestDocument>(WithoutHistory)
            .FirstOrDefaultAsync(cancellationToken);

    // One $in query on _id (uses the primary key index); the bulk update reads at most 100 requests this way.
    public async Task<IReadOnlyList<RequestDocument>> GetByIdsAsync(
        IReadOnlyCollection<ObjectId> ids,
        CancellationToken cancellationToken) =>
        await _collection
            .Find(Filter.In(x => x.Id, ids))
            .Project<RequestDocument>(WithoutHistory)
            .ToListAsync(cancellationToken);

    // Both groupings are computed by one $facet stage from the same collection scan.
    // This scans the whole collection (no index helps a full grouping), which is why the result is cached.
    // It is not a point-in-time snapshot: concurrent writes may or may not be reflected (snapshot reads need a replica set).
    public async Task<RequestSummaryCounts> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var byStatus = AggregateFacet.Create(
            ByStatusFacet,
            new EmptyPipelineDefinition<RequestDocument>()
                .Group(x => x.Status, g => new GroupCount<RequestStatus> { Key = g.Key, Count = g.Count() }));

        var byPriority = AggregateFacet.Create(
            ByPriorityFacet,
            new EmptyPipelineDefinition<RequestDocument>()
                .Group(x => x.Priority, g => new GroupCount<RequestPriority> { Key = g.Key, Count = g.Count() }));

        var result = await _collection.Aggregate()
            .Facet(byStatus, byPriority)
            .FirstOrDefaultAsync(cancellationToken);

        if (result is null)
        {
            return new RequestSummaryCounts(
                new Dictionary<RequestStatus, long>(),
                new Dictionary<RequestPriority, long>());
        }

        return new RequestSummaryCounts(
            ToDictionary(result.Facets.Single(f => f.Name == ByStatusFacet).Output<GroupCount<RequestStatus>>()),
            ToDictionary(result.Facets.Single(f => f.Name == ByPriorityFacet).Output<GroupCount<RequestPriority>>()));
    }

    // A single-document update is atomic in MongoDB. The filter includes the expected version, so of two concurrent
    // updates based on the same version only the first can match; status, updatedAt, version and the history entry
    // are written together in that one operation.
    public async Task<RequestDocument?> TryUpdateStatusAsync(
        ObjectId id,
        int expectedVersion,
        StatusChangeEntry change,
        CancellationToken cancellationToken)
    {
        var filter = Filter.And(
            Filter.Eq(x => x.Id, id),
            Filter.Eq(x => x.Version, expectedVersion));

        var update = Builders<RequestDocument>.Update
            .Set(x => x.Status, change.NewStatus)
            .Set(x => x.UpdatedAt, change.ChangedAt)
            .Inc(x => x.Version, 1)
            .Push(x => x.StatusHistory, change);

        return await _collection.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<RequestDocument>
            {
                ReturnDocument = ReturnDocument.After,
                Projection = WithoutHistory
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StatusChangeEntry>?> GetHistoryAsync(ObjectId id, CancellationToken cancellationToken)
    {
        var document = await _collection
            .Find(Filter.Eq(x => x.Id, id))
            .Project<RequestDocument>(Builders<RequestDocument>.Projection.Include(x => x.StatusHistory))
            .FirstOrDefaultAsync(cancellationToken);

        return document?.StatusHistory;
    }

    private static Dictionary<TKey, long> ToDictionary<TKey>(IEnumerable<GroupCount<TKey>> counts) where TKey : notnull =>
        counts.ToDictionary(x => x.Key, x => (long)x.Count);

    private static List<FilterDefinition<RequestDocument>> BuildFilters(RequestQueryParameters query)
    {
        var filters = new List<FilterDefinition<RequestDocument>>();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filters.Add(Filter.Text(query.Search.Trim()));
        }

        if (query.Status is { } status)
        {
            filters.Add(Filter.Eq(x => x.Status, status));
        }

        if (query.Priority is { } priority)
        {
            filters.Add(Filter.Eq(x => x.Priority, priority));
        }

        if (!string.IsNullOrEmpty(query.OrganizationName))
        {
            filters.Add(Filter.Eq(x => x.OrganizationName, query.OrganizationName));
        }

        if (!string.IsNullOrEmpty(query.AssignedTo))
        {
            filters.Add(Filter.Eq(x => x.AssignedTo, query.AssignedTo));
        }

        if (query.CreatedFromUtc is { } from)
        {
            filters.Add(Filter.Gte(x => x.CreatedAt, from));
        }

        if (query.CreatedToUtc is { } to)
        {
            filters.Add(Filter.Lt(x => x.CreatedAt, to));
        }

        return filters;
    }

    // Every sort ends with _id for stable pagination, and its key directions match an index
    // (or its exact reverse) so MongoDB can return the page in index order without an in-memory sort.
    private static SortDefinition<RequestDocument> BuildSort(RequestSortField field, SortDirection direction)
    {
        var ascending = direction == SortDirection.Asc;

        return field switch
        {
            RequestSortField.UpdatedAt => ascending
                ? Sort.Ascending(x => x.UpdatedAt).Ascending(x => x.Id)
                : Sort.Descending(x => x.UpdatedAt).Descending(x => x.Id),

            // Index ix_priority_createdAt_id is (priority 1, createdAt -1, _id -1). A descending priority sort
            // must therefore order createdAt ascending (oldest first); a mismatched direction was measured
            // as a collection scan with an in-memory sort (~330 ms on 100K documents).
            RequestSortField.Priority => ascending
                ? Sort.Ascending(x => x.Priority).Descending(x => x.CreatedAt).Descending(x => x.Id)
                : Sort.Descending(x => x.Priority).Ascending(x => x.CreatedAt).Ascending(x => x.Id),

            _ => ascending
                ? Sort.Ascending(x => x.CreatedAt).Ascending(x => x.Id)
                : Sort.Descending(x => x.CreatedAt).Descending(x => x.Id)
        };
    }

    private sealed class GroupCount<TKey>
    {
        public TKey Key { get; set; } = default!;

        public int Count { get; set; }
    }
}
