using RequestsManagement.Api.Tests.Infrastructure;

namespace RequestsManagement.Api.Tests.Integration;

// The API must refuse to start with an invalid summary cache TTL instead of running with an unusable cache.
// MongoDB is never contacted: the connection points to an unused local port and startup index creation is off.
public sealed class StartupValidationTests
{
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("01:00:01")]
    [InlineData("-00:00:30")]
    [InlineData("not-a-timespan")]
    public async Task Startup_WithInvalidSummaryCacheTimeToLive_Fails(string timeToLive)
    {
        await using var factory = new RequestsApiFactory(new Dictionary<string, string?>
        {
            ["MongoDb:ConnectionString"] = "mongodb://localhost:27999/?serverSelectionTimeoutMS=1500",
            ["MongoDb:DatabaseName"] = MongoTestDatabaseFixture.DatabaseNamePrefix + "startup_validation",
            ["MongoDb:EnsureIndexesOnStartup"] = "false",
            ["RequestSummaryCache:TimeToLive"] = timeToLive
        });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("TimeToLive", DescribeAll(exception));
    }

    private static string DescribeAll(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(inner => inner.Message));
            }
        }

        return string.Join(Environment.NewLine, messages);
    }
}
