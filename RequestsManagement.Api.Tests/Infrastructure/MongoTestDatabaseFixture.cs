using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Infrastructure.Mongo;
using RequestsManagement.Api.Infrastructure.Seeding;

namespace RequestsManagement.Api.Tests.Infrastructure;

// Creates a uniquely named database on the local MongoDB server, seeds it, and hosts the API against it.
// The database is dropped at the end only when this run provably created it:
// the name has the test prefix, the server is local, the name did not exist before the run,
// and the marker document written by this run is still present.
public sealed class MongoTestDatabaseFixture : IAsyncLifetime
{
    public const string DatabaseNamePrefix = "RequestsManagement_Tests_";
    public const int RequestCount = 500;

    private const string ConnectionString = "mongodb://localhost:27017";
    private const string MarkerCollectionName = "_testRunMarker";
    private const int GeneratorSeed = 4242;

    private readonly string _runId = Guid.NewGuid().ToString("N");
    private IMongoClient? _client;
    private bool _createdByThisRun;

    public string DatabaseName => DatabaseNamePrefix + _runId;

    public IReadOnlyList<RequestDocument> Requests { get; private set; } = [];

    public RequestsApiFactory Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            await CreateDatabaseAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_client is null)
        {
            return;
        }

        if (await IsSafeToDropAsync())
        {
            await _client.DropDatabaseAsync(DatabaseName);
        }
        else
        {
            Console.Error.WriteLine($"Test database '{DatabaseName}' was not dropped: it could not be proven that this run created it.");
        }
    }

    private async Task CreateDatabaseAsync()
    {
        RequestDataSeeder.EnsureLocalServer(ConnectionString);
        MongoServiceCollectionExtensions.RegisterConventions();

        _client = new MongoClient(ConnectionString);

        var existingNames = await (await _client.ListDatabaseNamesAsync()).ToListAsync();
        if (existingNames.Contains(DatabaseName))
        {
            throw new InvalidOperationException($"Test database '{DatabaseName}' already exists and will not be used.");
        }

        // The first write creates the database; the marker proves at cleanup time that this run created it.
        var database = _client.GetDatabase(DatabaseName);
        await database.GetCollection<BsonDocument>(MarkerCollectionName)
            .InsertOneAsync(new BsonDocument { ["_id"] = _runId, ["createdAt"] = DateTime.UtcNow });
        _createdByThisRun = true;

        var requests = new RequestDataGenerator(GeneratorSeed).Generate(RequestCount).ToList();
        await database.GetCollection<RequestDocument>(MongoServiceCollectionExtensions.RequestsCollectionName)
            .InsertManyAsync(requests);
        Requests = requests;

        Factory = new RequestsApiFactory(new Dictionary<string, string?>
        {
            ["MongoDb:ConnectionString"] = ConnectionString,
            ["MongoDb:DatabaseName"] = DatabaseName
        });

        // Creating the client starts the API; its startup creates the indexes in the test database.
        Client = Factory.CreateClient();

        var appDatabaseName = Factory.Services.GetRequiredService<IMongoCollection<RequestDocument>>()
            .Database.DatabaseNamespace.DatabaseName;
        if (!string.Equals(appDatabaseName, DatabaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The API under test is not connected to the test database.");
        }
    }

    private async Task<bool> IsSafeToDropAsync()
    {
        if (!_createdByThisRun || !DatabaseName.StartsWith(DatabaseNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            RequestDataSeeder.EnsureLocalServer(ConnectionString);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var marker = await _client!.GetDatabase(DatabaseName)
            .GetCollection<BsonDocument>(MarkerCollectionName)
            .Find(new BsonDocument("_id", _runId))
            .FirstOrDefaultAsync();

        return marker is not null;
    }
}

[CollectionDefinition(Name)]
public sealed class MongoIntegrationCollection : ICollectionFixture<MongoTestDatabaseFixture>
{
    public const string Name = "MongoDB integration";
}
