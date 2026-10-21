using System.Net;
using RequestsManagement.Api.Tests.Infrastructure;

namespace RequestsManagement.Api.Tests.Integration;

// Exercises the real failure path: the MongoDB driver cannot select a server, throws TimeoutException,
// and the global exception handler converts it to 503. No component is replaced.
public sealed class DatabaseUnavailableTests : IAsyncLifetime
{
    // Nothing listens on this local port; a short server selection timeout keeps the test fast.
    private const string UnreachableConnectionString = "mongodb://localhost:27999/?serverSelectionTimeoutMS=1500";

    private readonly RequestsApiFactory _factory = new(new Dictionary<string, string?>
    {
        ["MongoDb:ConnectionString"] = UnreachableConnectionString,
        ["MongoDb:DatabaseName"] = MongoTestDatabaseFixture.DatabaseNamePrefix + "unreachable",
        ["MongoDb:EnsureIndexesOnStartup"] = "false"
    });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/api/requests")]
    [InlineData("/api/requests/000000000000000000000000")]
    [InlineData("/api/requests/summary")]
    public async Task Request_WhenDatabaseIsUnreachable_Returns503ProblemDetails(string url)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(url);
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal("The database is temporarily unavailable.", problem.GetProperty("title").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.False(problem.TryGetProperty("detail", out _), "Internal exception details must not be returned.");
    }
}
