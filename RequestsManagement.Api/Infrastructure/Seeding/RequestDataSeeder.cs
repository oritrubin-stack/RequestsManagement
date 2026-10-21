using System.Diagnostics;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Infrastructure.Mongo;

namespace RequestsManagement.Api.Infrastructure.Seeding;

// Recreates the requests collection with deterministic test data.
// Destructive by design, so it only runs against the approved database on a local server,
// and it only ever drops the requests collection.
public sealed class RequestDataSeeder
{
    public const string AllowedDatabaseName = "RequestsManagement";
    public const int DefaultRequestCount = 100_000;

    private const int BatchSize = 5_000;

    private static readonly HashSet<string> LocalHosts =
        new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", "[::1]" };

    private readonly IMongoCollection<RequestDocument> _collection;
    private readonly RequestIndexes _indexes;
    private readonly ILogger<RequestDataSeeder> _logger;

    public RequestDataSeeder(
        IMongoCollection<RequestDocument> collection,
        RequestIndexes indexes,
        ILogger<RequestDataSeeder> logger)
    {
        _collection = collection;
        _indexes = indexes;
        _logger = logger;
    }

    // Validates the target before resolving any MongoDB service, so a rejected target is never contacted.
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<IOptions<MongoDbOptions>>().Value;
        EnsureTargetIsAllowed(options);

        var seeder = services.GetRequiredService<RequestDataSeeder>();
        await seeder.SeedAsync(DefaultRequestCount, cancellationToken);
    }

    public static void EnsureTargetIsAllowed(MongoDbOptions options)
    {
        if (!string.Equals(options.DatabaseName, AllowedDatabaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Seeding is allowed only for the database '{AllowedDatabaseName}'.");
        }

        EnsureLocalServer(options.ConnectionString);
    }

    // Shared with the integration test fixture, which applies the same rule before creating or dropping a test database.
    public static void EnsureLocalServer(string connectionString)
    {
        var url = MongoUrl.Create(connectionString);

        // SRV, replica set and load-balanced modes let the driver connect to servers other than the listed hosts,
        // so they are rejected to keep the listed local host the only possible target.
        if (url.Scheme == ConnectionStringScheme.MongoDBPlusSrv ||
            !string.IsNullOrEmpty(url.ReplicaSetName) ||
            url.LoadBalanced)
        {
            throw new InvalidOperationException(
                "Seeding does not allow SRV, replica set or load-balanced connection strings.");
        }

        if (!url.Servers.Any() || !url.Servers.All(server => LocalHosts.Contains(server.Host)))
        {
            throw new InvalidOperationException("Seeding is allowed only against a local MongoDB server.");
        }
    }

    public async Task SeedAsync(int count, CancellationToken cancellationToken)
    {
        var databaseName = _collection.Database.DatabaseNamespace.DatabaseName;
        var collectionName = _collection.CollectionNamespace.CollectionName;

        // Defense in depth: the injected collection must belong to the approved database.
        if (!string.Equals(databaseName, AllowedDatabaseName, StringComparison.Ordinal) ||
            !string.Equals(collectionName, MongoServiceCollectionExtensions.RequestsCollectionName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Seeding target does not match the approved database and collection.");
        }

        var stopwatch = Stopwatch.StartNew();

        // Dropping the collection also drops its indexes, so they are rebuilt once after the bulk load.
        _logger.LogInformation("Dropping collection {CollectionName} in database {DatabaseName}", collectionName, databaseName);
        await _collection.Database.DropCollectionAsync(collectionName, cancellationToken);

        var generator = new RequestDataGenerator();
        var inserted = 0;

        foreach (var batch in generator.Generate(count).Chunk(BatchSize))
        {
            await _collection.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, cancellationToken);
            inserted += batch.Length;

            if (inserted % 20_000 == 0 || inserted == count)
            {
                _logger.LogInformation("Inserted {Inserted} of {Count} requests", inserted, count);
            }
        }

        _logger.LogInformation("Creating indexes after bulk load");
        await _indexes.EnsureIndexesAsync(cancellationToken);

        _logger.LogInformation(
            "Seeding completed: {Count} requests in {ElapsedSeconds:F1} seconds",
            inserted,
            stopwatch.Elapsed.TotalSeconds);
    }
}
