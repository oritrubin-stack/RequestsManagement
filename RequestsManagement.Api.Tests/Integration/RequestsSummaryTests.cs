using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests;
using RequestsManagement.Api.Features.Requests.Dtos;
using RequestsManagement.Api.Infrastructure.Mongo;
using RequestsManagement.Api.Tests.Infrastructure;

namespace RequestsManagement.Api.Tests.Integration;

[Collection(MongoIntegrationCollection.Name)]
public sealed class RequestsSummaryTests
{
    private readonly MongoTestDatabaseFixture _fixture;
    private readonly IRequestSummaryCache _summaryCache;

    public RequestsSummaryTests(MongoTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _summaryCache = fixture.Factory.Services.GetRequiredService<IRequestSummaryCache>();
    }

    [Fact]
    public async Task Summary_MatchesSeededRequests()
    {
        // Another test may have cached a summary in the shared API instance.
        _summaryCache.Invalidate();

        var summary = await GetSummaryAsync();

        Assert.Equal(MongoTestDatabaseFixture.RequestCount, summary.TotalCount);
        Assert.Equal(
            Enum.GetValues<RequestStatus>().Select(status => (status, (long)_fixture.Requests.Count(x => x.Status == status))),
            summary.ByStatus.Select(x => (x.Status, x.Count)));
        Assert.Equal(
            Enum.GetValues<RequestPriority>().Select(priority => (priority, (long)_fixture.Requests.Count(x => x.Priority == priority))),
            summary.ByPriority.Select(x => (x.Priority, x.Count)));
        Assert.Equal(summary.TotalCount, summary.ByPriority.Sum(x => x.Count));
    }

    [Fact]
    public async Task Summary_IsServedFromCacheUntilInvalidated()
    {
        var database = _fixture.Database;
        Assert.StartsWith(MongoTestDatabaseFixture.DatabaseNamePrefix, database.DatabaseNamespace.DatabaseName);

        var collection = database.GetCollection<RequestDocument>(MongoServiceCollectionExtensions.RequestsCollectionName);
        var temporary = new RequestDocument
        {
            Title = "Temporary summary test request",
            OrganizationName = "Summary Test Org",
            Status = RequestStatus.New,
            Priority = RequestPriority.Low,
            AssignedTo = "summary.test",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Version = 1
        };

        _summaryCache.Invalidate();
        var before = await GetSummaryAsync();
        var inserted = false;

        try
        {
            await collection.InsertOneAsync(temporary);
            inserted = true;

            var cached = await GetSummaryAsync();
            Assert.Equal(before.TotalCount, cached.TotalCount);
            Assert.Equal(before.GeneratedAt, cached.GeneratedAt);

            _summaryCache.Invalidate();
            var refreshed = await GetSummaryAsync();
            Assert.Equal(before.TotalCount + 1, refreshed.TotalCount);
            Assert.Equal(StatusCount(before, RequestStatus.New) + 1, StatusCount(refreshed, RequestStatus.New));
        }
        finally
        {
            // Cleanup is verified only when the insert succeeded; otherwise there is nothing to delete
            // and the original failure must not be replaced by a cleanup message.
            if (inserted)
            {
                var deleted = await collection.DeleteOneAsync(x => x.Id == temporary.Id);
                _summaryCache.Invalidate();

                Assert.True(
                    deleted.DeletedCount == 1,
                    $"Cleanup failed: temporary document {temporary.Id} was not deleted from {database.DatabaseNamespace.DatabaseName}.");
            }
            else
            {
                _summaryCache.Invalidate();
            }
        }

        var afterCleanup = await GetSummaryAsync();
        Assert.Equal(before.TotalCount, afterCleanup.TotalCount);
    }

    [Fact]
    public async Task RepositorySummary_ForEmptyAndMissingCollections_ReturnsNoCounts()
    {
        var database = _fixture.Database;
        Assert.StartsWith(MongoTestDatabaseFixture.DatabaseNamePrefix, database.DatabaseNamespace.DatabaseName);

        var emptyName = "empty_" + ObjectId.GenerateNewId();
        await database.CreateCollectionAsync(emptyName);

        var empty = await new RequestRepository(database.GetCollection<RequestDocument>(emptyName))
            .GetSummaryAsync(CancellationToken.None);
        var missing = await new RequestRepository(database.GetCollection<RequestDocument>("missing_" + ObjectId.GenerateNewId()))
            .GetSummaryAsync(CancellationToken.None);

        Assert.Empty(empty.ByStatus);
        Assert.Empty(empty.ByPriority);
        Assert.Empty(missing.ByStatus);
        Assert.Empty(missing.ByPriority);
    }

    private async Task<RequestSummaryDto> GetSummaryAsync()
    {
        var response = await _fixture.Client.GetAsync("/api/requests/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsAsync<RequestSummaryDto>();
    }

    private static long StatusCount(RequestSummaryDto summary, RequestStatus status) =>
        summary.ByStatus.Single(x => x.Status == status).Count;
}
