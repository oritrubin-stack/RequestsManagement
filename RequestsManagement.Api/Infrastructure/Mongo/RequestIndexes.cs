using MongoDB.Driver;
using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Infrastructure.Mongo;

// Index definitions for the requests collection. Every index that serves sorting ends with _id,
// which is the tie-breaker for stable pagination, so sorting never requires an in-memory sort stage.
public sealed class RequestIndexes
{
    private readonly IMongoCollection<RequestDocument> _collection;
    private readonly ILogger<RequestIndexes> _logger;

    public RequestIndexes(IMongoCollection<RequestDocument> collection, ILogger<RequestIndexes> logger)
    {
        _collection = collection;
        _logger = logger;
    }

    // Idempotent: creating an index that already exists with the same definition is a no-op.
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var keys = Builders<RequestDocument>.IndexKeys;

        var models = new[]
        {
            // Default sort (newest first) and created-date range filtering.
            CreateIndex(keys.Descending(x => x.CreatedAt).Descending(x => x.Id), "ix_createdAt_id"),

            // Sorting by last update.
            CreateIndex(keys.Descending(x => x.UpdatedAt).Descending(x => x.Id), "ix_updatedAt_id"),

            // Filtering and sorting by priority.
            CreateIndex(
                keys.Ascending(x => x.Priority).Descending(x => x.CreatedAt).Descending(x => x.Id),
                "ix_priority_createdAt_id"),

            // Filtering by status, the most common filter, with the default sort.
            CreateIndex(
                keys.Ascending(x => x.Status).Descending(x => x.CreatedAt).Descending(x => x.Id),
                "ix_status_createdAt_id"),

            // Filtering by assignee with the default sort.
            CreateIndex(
                keys.Ascending(x => x.AssignedTo).Descending(x => x.CreatedAt).Descending(x => x.Id),
                "ix_assignedTo_createdAt_id"),

            // Exact-match filtering by organization with the default sort.
            CreateIndex(
                keys.Ascending(x => x.OrganizationName).Descending(x => x.CreatedAt).Descending(x => x.Id),
                "ix_organizationName_createdAt_id"),

            // Free-text search on whole words. Language "none" disables stemming and stop words.
            new CreateIndexModel<RequestDocument>(
                keys.Text(x => x.Title).Text(x => x.OrganizationName),
                new CreateIndexOptions { Name = "ix_text_title_organizationName", DefaultLanguage = "none" })
        };

        var created = await _collection.Indexes.CreateManyAsync(models, cancellationToken);

        _logger.LogInformation(
            "Ensured {IndexCount} indexes on collection {CollectionName}",
            created.Count(),
            _collection.CollectionNamespace.CollectionName);
    }

    private static CreateIndexModel<RequestDocument> CreateIndex(
        IndexKeysDefinition<RequestDocument> keys,
        string name) =>
        new(keys, new CreateIndexOptions { Name = name });
}
