using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests;
using RequestsManagement.Api.Features.Requests.Dtos;
using RequestsManagement.Api.Infrastructure.Mongo;
using RequestsManagement.Api.Tests.Infrastructure;
using Xunit.Abstractions;

namespace RequestsManagement.Api.Tests.Integration;

// Runs against the write test database. Each test creates the requests it changes.
[Collection(MongoWriteIntegrationCollection.Name)]
public sealed class RequestsBulkStatusUpdateTests
{
    private const string ChangedBy = "dana.cohen";

    private readonly MongoTestDatabaseFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly IMongoCollection<RequestDocument> _collection;

    public RequestsBulkStatusUpdateTests(MongoTestDatabaseFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        Assert.StartsWith(MongoTestDatabaseFixture.DatabaseNamePrefix, fixture.Database.DatabaseNamespace.DatabaseName);
        _collection = fixture.Database.GetCollection<RequestDocument>(MongoServiceCollectionExtensions.RequestsCollectionName);
    }

    [Fact]
    public async Task Repository_GetByIds_ReturnsOnlyRequestedExistingRequestsWithoutHistory()
    {
        var withHistory = _fixture.Requests.Where(x => x.StatusHistory.Count > 0).Take(3).ToList();
        var missing = ObjectId.GenerateNewId();

        var found = await new RequestRepository(_collection)
            .GetByIdsAsync([.. withHistory.Select(x => x.Id), missing], CancellationToken.None);

        Assert.Equal(withHistory.Select(x => x.Id).Order(), found.Select(x => x.Id).Order());
        Assert.All(found, x => Assert.Empty(x.StatusHistory));
        Assert.All(found, x => Assert.True(x.Version > 1));
    }

    [Fact]
    public async Task MixedBatch_ReturnsEachOutcomeInRequestOrder_AndAppliesOnlyValidChanges()
    {
        var updatable = await CreateRequestAsync();
        var stale = await CreateRequestAsync();
        var final = await CreateCompletedRequestAsync();
        var missing = ObjectId.GenerateNewId();

        var response = await PostBulkAsync(
            Item(updatable.Id, RequestStatus.InProgress, 1),
            Item(missing, RequestStatus.InProgress, 1),
            Item(stale.Id, RequestStatus.InProgress, 2),
            Item(final.Id, RequestStatus.InProgress, 3));
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<BulkUpdateStatusResponse>(raw, HttpResponseExtensions.JsonOptions)!;
        var json = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((1, 1, 1, 1), (body.UpdatedCount, body.NotFoundCount, body.VersionConflictCount, body.InvalidTransitionCount));
        Assert.Equal(
            [updatable.Id.ToString(), missing.ToString(), stale.Id.ToString(), final.Id.ToString()],
            body.Results.Select(x => x.Id));
        Assert.Equal(
            [StatusUpdateOutcome.Updated, StatusUpdateOutcome.NotFound, StatusUpdateOutcome.VersionConflict, StatusUpdateOutcome.InvalidTransition],
            body.Results.Select(x => x.Outcome));

        Assert.Equal(2, body.Results[0].Request!.Version);
        Assert.Equal(RequestStatus.InProgress, body.Results[0].Request!.Status);
        Assert.False(json.GetProperty("results")[1].TryGetProperty("request", out _));
        Assert.Equal(1, body.Results[2].CurrentVersion);
        Assert.Equal(RequestStatus.Completed, body.Results[3].CurrentStatus);
        Assert.Contains("final", body.Results[3].Message);

        var updated = await LoadAsync(updatable.Id);
        Assert.Equal(2, updated.Version);
        var entry = Assert.Single(updated.StatusHistory);
        Assert.Equal((RequestStatus.New, RequestStatus.InProgress, ChangedBy), (entry.PreviousStatus, entry.NewStatus, entry.ChangedBy));
        AssertUnchanged(stale, await LoadAsync(stale.Id));
        AssertUnchanged(final, await LoadAsync(final.Id));
    }

    [Fact]
    public async Task HundredItems_AreAllApplied()
    {
        var requests = await CreateRequestsAsync(BulkUpdateStatusRequest.MaxItems);

        var stopwatch = Stopwatch.StartNew();
        var response = await PostBulkAsync(requests.Select(x => Item(x.Id, RequestStatus.InProgress, 1)).ToArray());
        stopwatch.Stop();
        var body = await response.ReadAsAsync<BulkUpdateStatusResponse>();
        _output.WriteLine($"Bulk update of {requests.Count} items took {stopwatch.ElapsedMilliseconds} ms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BulkUpdateStatusRequest.MaxItems, body.UpdatedCount);
        var ids = requests.Select(x => x.Id).ToList();
        Assert.Equal(
            BulkUpdateStatusRequest.MaxItems,
            await _collection.CountDocumentsAsync(x => ids.Contains(x.Id) && x.Version == 2 && x.StatusHistory.Count == 1));
    }

    [Fact]
    public async Task HundredAndOneItems_Returns400_AndChangesNothing()
    {
        var requests = await CreateRequestsAsync(BulkUpdateStatusRequest.MaxItems + 1);

        var response = await PostBulkAsync(requests.Select(x => Item(x.Id, RequestStatus.InProgress, 1)).ToArray());
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("Items", out _));
        var ids = requests.Select(x => x.Id).ToList();
        Assert.Equal(0, await _collection.CountDocumentsAsync(x => ids.Contains(x.Id) && x.Version != 1));
    }

    // Each body contains a valid change to a real request ({id}); the test verifies it was not applied.
    [Theory]
    [InlineData("""{ "items": [] }""", ChangedBy, "Items")]
    [InlineData("""{ }""", ChangedBy, "Items")]
    [InlineData("""{ "items": [ { "id": "{id}", "status": "InProgress", "expectedVersion": 1 }, { "id": "not-an-id", "status": "InProgress", "expectedVersion": 1 } ] }""", ChangedBy, "Items[1].Id")]
    [InlineData("""{ "items": [ { "id": "{id}", "status": "InProgress", "expectedVersion": 1 }, { "id": "{other}", "expectedVersion": 1 } ] }""", ChangedBy, "Items[1].Status")]
    [InlineData("""{ "items": [ { "id": "{id}", "status": "InProgress", "expectedVersion": 1 }, { "id": "{other}", "status": "Closed", "expectedVersion": 1 } ] }""", ChangedBy, "$.items[1].status")]
    [InlineData("""{ "items": [ { "id": "{id}", "status": "InProgress", "expectedVersion": 1 }, { "id": "{other}", "status": "InProgress", "expectedVersion": 0 } ] }""", ChangedBy, "Items[1].ExpectedVersion")]
    [InlineData("""{ "items": [ { "id": "{id}", "status": "InProgress", "expectedVersion": 1 }, { "id": "{id}", "status": "Waiting", "expectedVersion": 1 } ] }""", ChangedBy, "Items")]
    [InlineData("""{ "items": [ { "id": "{id}", "status": "InProgress", "expectedVersion": 1 } ] }""", null, "X-User-Name")]
    public async Task StructurallyInvalidRequest_Returns400_AndProcessesNoItem(string bodyTemplate, string? changedBy, string expectedErrorKey)
    {
        var request = await CreateRequestAsync();
        var body = bodyTemplate
            .Replace("{id}", request.Id.ToString())
            .Replace("{other}", ObjectId.GenerateNewId().ToString());

        var response = await PostRawAsync(body, changedBy);
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(
            problem.GetProperty("errors").TryGetProperty(expectedErrorKey, out _),
            $"Expected an error for '{expectedErrorKey}', got: {problem.GetProperty("errors")}");
        AssertUnchanged(request, await LoadAsync(request.Id));
    }

    [Fact]
    public async Task DuplicateIdsInDifferentLetterCase_AreRejected()
    {
        var request = await CreateRequestAsync();
        var id = request.Id.ToString();

        var response = await PostRawAsync(
            $$"""{ "items": [ { "id": "{{id.ToLowerInvariant()}}", "status": "InProgress", "expectedVersion": 1 }, { "id": "{{id.ToUpperInvariant()}}", "status": "InProgress", "expectedVersion": 1 } ] }""",
            ChangedBy);
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(id, problem.GetProperty("errors").GetProperty("Items")[0].GetString());
        AssertUnchanged(request, await LoadAsync(request.Id));
    }

    [Fact]
    public async Task BatchWithoutSuccessfulUpdate_DoesNotInvalidateSummary()
    {
        var stale = await CreateRequestAsync();
        var cache = _fixture.Factory.Services.GetRequiredService<IRequestSummaryCache>();
        cache.Invalidate();
        var before = await GetSummaryAsync();

        var response = await PostBulkAsync(
            Item(stale.Id, RequestStatus.InProgress, 5),
            Item(ObjectId.GenerateNewId(), RequestStatus.InProgress, 1));
        var body = await response.ReadAsAsync<BulkUpdateStatusResponse>();
        var after = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.UpdatedCount);
        Assert.Equal(before.GeneratedAt, after.GeneratedAt);
    }

    [Fact]
    public async Task BatchWithSuccessfulUpdates_InvalidatesSummary()
    {
        var first = await CreateRequestAsync();
        var second = await CreateRequestAsync();
        var cache = _fixture.Factory.Services.GetRequiredService<IRequestSummaryCache>();
        cache.Invalidate();
        var before = await GetSummaryAsync();

        var response = await PostBulkAsync(
            Item(first.Id, RequestStatus.InProgress, 1),
            Item(second.Id, RequestStatus.InProgress, 1),
            Item(ObjectId.GenerateNewId(), RequestStatus.InProgress, 1));
        var after = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(before.GeneratedAt, after.GeneratedAt);
        Assert.Equal(Count(before, RequestStatus.New) - 2, Count(after, RequestStatus.New));
        Assert.Equal(Count(before, RequestStatus.InProgress) + 2, Count(after, RequestStatus.InProgress));
    }

    [Fact]
    public async Task TwoConcurrentBatchesOnSameRequests_UpdateEachRequestExactlyOnce()
    {
        var requests = await CreateRequestsAsync(20);
        var items = requests.Select(x => Item(x.Id, RequestStatus.InProgress, 1)).ToArray();

        var responses = await Task.WhenAll(PostBulkAsync(items), PostBulkAsync(items));
        var bodies = await Task.WhenAll(responses.Select(x => x.ReadAsAsync<BulkUpdateStatusResponse>()));

        Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        Assert.Equal(20, bodies.Sum(x => x.UpdatedCount));
        Assert.Equal(20, bodies.Sum(x => x.VersionConflictCount));
        for (var i = 0; i < requests.Count; i++)
        {
            Assert.Single(bodies, body => body.Results[i].Outcome == StatusUpdateOutcome.Updated);
        }

        var ids = requests.Select(x => x.Id).ToList();
        Assert.Equal(20, await _collection.CountDocumentsAsync(x => ids.Contains(x.Id) && x.Version == 2 && x.StatusHistory.Count == 1));
    }

    [Fact]
    public async Task ConcurrentBatchAndSingleUpdate_OnSameRequest_ExactlyOneSucceeds()
    {
        var request = await CreateRequestAsync();

        var bulkTask = PostBulkAsync(Item(request.Id, RequestStatus.InProgress, 1));
        var singleTask = PutStatusAsync(request.Id, RequestStatus.InProgress, 1);
        await Task.WhenAll(bulkTask, singleTask);
        var bulk = await bulkTask.Result.ReadAsAsync<BulkUpdateStatusResponse>();

        var successes = bulk.UpdatedCount + (singleTask.Result.StatusCode == HttpStatusCode.OK ? 1 : 0);
        var stored = await LoadAsync(request.Id);

        Assert.Equal(1, successes);
        Assert.Equal(2, stored.Version);
        Assert.Single(stored.StatusHistory);
    }

    private async Task<RequestDocument> CreateRequestAsync() => (await CreateRequestsAsync(1))[0];

    private async Task<List<RequestDocument>> CreateRequestsAsync(int count)
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var requests = Enumerable.Range(0, count)
            .Select(i => new RequestDocument
            {
                Title = $"Bulk test request {i}",
                OrganizationName = "Bulk Test Org",
                Status = RequestStatus.New,
                Priority = RequestPriority.Low,
                AssignedTo = "bulk.test",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                Version = 1
            })
            .ToList();

        await _collection.InsertManyAsync(requests);
        return requests;
    }

    // Reaches Completed through the API, so the request has a consistent history (version 3).
    private async Task<RequestDocument> CreateCompletedRequestAsync()
    {
        var request = await CreateRequestAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutStatusAsync(request.Id, RequestStatus.InProgress, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutStatusAsync(request.Id, RequestStatus.Completed, 2)).StatusCode);
        return await LoadAsync(request.Id);
    }

    private async Task<RequestDocument> LoadAsync(ObjectId id) =>
        await _collection.Find(x => x.Id == id).SingleAsync();

    private static object Item(ObjectId id, RequestStatus status, int expectedVersion) =>
        new { id = id.ToString(), status = status.ToString(), expectedVersion };

    private Task<HttpResponseMessage> PostBulkAsync(params object[] items) =>
        SendAsync(HttpMethod.Post, "/api/requests/status/bulk", JsonContent.Create(new { items }), ChangedBy);

    private Task<HttpResponseMessage> PostRawAsync(string json, string? changedBy) =>
        SendAsync(HttpMethod.Post, "/api/requests/status/bulk", new StringContent(json, Encoding.UTF8, "application/json"), changedBy);

    private Task<HttpResponseMessage> PutStatusAsync(ObjectId id, RequestStatus status, int expectedVersion) =>
        SendAsync(HttpMethod.Put, $"/api/requests/{id}/status", JsonContent.Create(new { status = status.ToString(), expectedVersion }), ChangedBy);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent content, string? changedBy)
    {
        var message = new HttpRequestMessage(method, url) { Content = content };
        if (changedBy is not null)
        {
            message.Headers.Add(RequestsController.ChangedByHeader, changedBy);
        }

        return _fixture.Client.SendAsync(message);
    }

    private async Task<RequestSummaryDto> GetSummaryAsync() =>
        await (await _fixture.Client.GetAsync("/api/requests/summary")).ReadAsAsync<RequestSummaryDto>();

    private static long Count(RequestSummaryDto summary, RequestStatus status) =>
        summary.ByStatus.Single(x => x.Status == status).Count;

    private static void AssertUnchanged(RequestDocument expected, RequestDocument actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.StatusHistory.Count, actual.StatusHistory.Count);
    }
}
