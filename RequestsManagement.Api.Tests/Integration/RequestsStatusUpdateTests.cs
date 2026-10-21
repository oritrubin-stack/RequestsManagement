using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests;
using RequestsManagement.Api.Features.Requests.Dtos;
using RequestsManagement.Api.Infrastructure.Mongo;
using RequestsManagement.Api.Tests.Infrastructure;

namespace RequestsManagement.Api.Tests.Integration;

// Runs against its own test database (write collection), so changes never affect the read-only tests.
// Each test creates the requests it changes.
[Collection(MongoWriteIntegrationCollection.Name)]
public sealed class RequestsStatusUpdateTests
{
    private const string ChangedBy = "dana.cohen";

    private readonly MongoTestDatabaseFixture _fixture;
    private readonly IMongoCollection<RequestDocument> _collection;

    public RequestsStatusUpdateTests(MongoTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        Assert.StartsWith(MongoTestDatabaseFixture.DatabaseNamePrefix, fixture.Database.DatabaseNamespace.DatabaseName);
        _collection = fixture.Database.GetCollection<RequestDocument>(MongoServiceCollectionExtensions.RequestsCollectionName);
    }

    [Fact]
    public async Task UpdateStatus_ValidTransition_UpdatesStatusVersionTimestampAndHistoryAtomically()
    {
        var request = await CreateRequestAsync();

        var response = await PutStatusAsync(request.Id.ToString(), RequestStatus.InProgress, expectedVersion: 1);
        var dto = await response.ReadAsAsync<RequestDto>();
        var stored = await LoadAsync(request.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RequestStatus.InProgress, dto.Status);
        Assert.Equal(2, dto.Version);
        Assert.True(dto.UpdatedAt > request.UpdatedAt);

        Assert.Equal(RequestStatus.InProgress, stored.Status);
        Assert.Equal(2, stored.Version);
        Assert.Equal(dto.UpdatedAt, stored.UpdatedAt);
        var entry = Assert.Single(stored.StatusHistory);
        Assert.Equal(RequestStatus.New, entry.PreviousStatus);
        Assert.Equal(RequestStatus.InProgress, entry.NewStatus);
        Assert.Equal(ChangedBy, entry.ChangedBy);
        Assert.Equal(stored.UpdatedAt, entry.ChangedAt);
    }

    [Fact]
    public async Task UpdateStatus_SequenceOfTransitions_IsRecordedInOrderAndReturnedByHistory()
    {
        var request = await CreateRequestAsync();
        RequestStatus[] path = [RequestStatus.InProgress, RequestStatus.Waiting, RequestStatus.InProgress, RequestStatus.Completed];

        for (var i = 0; i < path.Length; i++)
        {
            var response = await PutStatusAsync(request.Id.ToString(), path[i], expectedVersion: i + 1);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var historyResponse = await _fixture.Client.GetAsync($"/api/requests/{request.Id}/history");
        var history = await historyResponse.ReadAsAsync<List<StatusChangeDto>>();
        var stored = await LoadAsync(request.Id);

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.Equal(5, stored.Version);
        Assert.Equal(RequestStatus.Completed, stored.Status);
        Assert.Equal(
            [(RequestStatus.New, RequestStatus.InProgress), (RequestStatus.InProgress, RequestStatus.Waiting),
             (RequestStatus.Waiting, RequestStatus.InProgress), (RequestStatus.InProgress, RequestStatus.Completed)],
            history.Select(x => (x.PreviousStatus, x.NewStatus)));
        Assert.All(history, x => Assert.Equal(request.Id.ToString(), x.RequestId));
        Assert.Equal(history.Select(x => x.ChangedAt).Order(), history.Select(x => x.ChangedAt));
        Assert.Equal(stored.UpdatedAt, history[^1].ChangedAt);
    }

    [Theory]
    [InlineData(RequestStatus.Completed)]
    [InlineData(RequestStatus.New)]
    public async Task UpdateStatus_DisallowedOrSameStatus_Returns400AndLeavesRequestUnchanged(RequestStatus target)
    {
        var request = await CreateRequestAsync();

        var response = await PutStatusAsync(request.Id.ToString(), target, expectedVersion: 1);
        var problem = await response.ReadJsonAsync();
        var stored = await LoadAsync(request.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(problem.GetProperty("errors").TryGetProperty("Status", out _));
        AssertUnchanged(request, stored);
    }

    [Fact]
    public async Task UpdateStatus_FromFinalStatus_Returns400()
    {
        var request = await CreateRequestAsync();
        await PutStatusAsync(request.Id.ToString(), RequestStatus.InProgress, 1);
        await PutStatusAsync(request.Id.ToString(), RequestStatus.Completed, 2);

        var response = await PutStatusAsync(request.Id.ToString(), RequestStatus.InProgress, expectedVersion: 3);
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("final", problem.GetProperty("errors").GetProperty("Status")[0].GetString());
        Assert.Equal(3, (await LoadAsync(request.Id)).Version);
    }

    [Fact]
    public async Task UpdateStatus_StaleVersion_Returns409WithCurrentStateAndLeavesRequestUnchanged()
    {
        var request = await CreateRequestAsync();
        await PutStatusAsync(request.Id.ToString(), RequestStatus.InProgress, 1);
        var beforeConflict = await LoadAsync(request.Id);

        var response = await PutStatusAsync(request.Id.ToString(), RequestStatus.Waiting, expectedVersion: 1);
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal(2, problem.GetProperty("currentVersion").GetInt32());
        Assert.Equal("InProgress", problem.GetProperty("currentStatus").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
        AssertUnchanged(beforeConflict, await LoadAsync(request.Id));
    }

    [Fact]
    public async Task UpdateStatus_UnknownRequest_Returns404()
    {
        var response = await PutStatusAsync(ObjectId.GenerateNewId().ToString(), RequestStatus.InProgress, 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("""{ "expectedVersion": 1 }""", ChangedBy, "Status")]
    [InlineData("""{ "status": "InProgress" }""", ChangedBy, "ExpectedVersion")]
    [InlineData("""{ "status": "InProgress", "expectedVersion": 0 }""", ChangedBy, "ExpectedVersion")]
    [InlineData("""{ "status": 7, "expectedVersion": 1 }""", ChangedBy, "Status")]
    [InlineData("""{ "status": "Closed", "expectedVersion": 1 }""", ChangedBy, "status")]
    [InlineData("""{ "status": "InProgress", "expectedVersion": 1 }""", null, "X-User-Name")]
    [InlineData("""{ "status": "InProgress", "expectedVersion": 1 }""", "dana cohen", "X-User-Name")]
    [InlineData("""{ "status": "InProgress", "expectedVersion": 1 }""", "dana<script>", "X-User-Name")]
    public async Task UpdateStatus_InvalidInput_Returns400WithFieldErrorAndLeavesRequestUnchanged(
        string body,
        string? changedBy,
        string expectedErrorKey)
    {
        var request = await CreateRequestAsync();

        var response = await SendAsync(request.Id.ToString(), body, changedBy);
        var problem = await response.ReadJsonAsync();
        var errorKeys = problem.GetProperty("errors").EnumerateObject().Select(x => x.Name).ToList();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(errorKeys, key => key.Contains(expectedErrorKey, StringComparison.OrdinalIgnoreCase));
        AssertUnchanged(request, await LoadAsync(request.Id));
    }

    [Fact]
    public async Task UpdateStatus_TooLongChangedBy_Returns400NamingTheHeader()
    {
        var request = await CreateRequestAsync();

        var response = await SendAsync(request.Id.ToString(), """{ "status": "InProgress", "expectedVersion": 1 }""", new string('a', 101));
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "X-User-Name must be at most 100 characters.",
            problem.GetProperty("errors").GetProperty("X-User-Name")[0].GetString());
        AssertUnchanged(request, await LoadAsync(request.Id));
    }

    [Fact]
    public async Task UpdateStatus_MissingChangedBy_Returns400NamingTheHeader()
    {
        var request = await CreateRequestAsync();

        var response = await SendAsync(request.Id.ToString(), """{ "status": "InProgress", "expectedVersion": 1 }""", changedBy: null);
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "The X-User-Name header is required.",
            problem.GetProperty("errors").GetProperty("X-User-Name")[0].GetString());
    }

    [Fact]
    public async Task UpdateStatus_UnknownStatusValue_Returns400WithoutInternalTypeNames()
    {
        var request = await CreateRequestAsync();

        var response = await SendAsync(request.Id.ToString(), """{ "status": "Closed", "expectedVersion": 1 }""", ChangedBy);
        var body = await response.Content.ReadAsStringAsync();
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(problem.GetProperty("errors").TryGetProperty("$.status", out _));
        Assert.DoesNotContain("System.", body);
        Assert.DoesNotContain("RequestsManagement.", body);
        Assert.DoesNotContain("Nullable", body);
        AssertUnchanged(request, await LoadAsync(request.Id));
    }

    [Fact]
    public async Task UpdateStatus_InvalidId_Returns400()
    {
        var response = await PutStatusAsync("not-an-id", RequestStatus.InProgress, 1);
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("id", out _));
    }

    [Fact]
    public async Task UpdateStatus_ConcurrentRequestsWithSameVersion_ExactlyOneSucceeds()
    {
        const int concurrentRequests = 10;
        var request = await CreateRequestAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, concurrentRequests)
            .Select(_ => PutStatusAsync(request.Id.ToString(), RequestStatus.InProgress, expectedVersion: 1)));
        var stored = await LoadAsync(request.Id);

        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(concurrentRequests - 1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2, stored.Version);
        Assert.Single(stored.StatusHistory);
    }

    [Fact]
    public async Task Repository_ConditionalUpdate_MatchesOnlyTheExpectedVersion()
    {
        var request = await CreateRequestAsync();
        var repository = new RequestRepository(_collection);
        var change = new StatusChangeEntry
        {
            PreviousStatus = RequestStatus.New,
            NewStatus = RequestStatus.InProgress,
            ChangedAt = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc),
            ChangedBy = ChangedBy
        };

        var first = await repository.TryUpdateStatusAsync(request.Id, 1, change, CancellationToken.None);
        var second = await repository.TryUpdateStatusAsync(request.Id, 1, change, CancellationToken.None);
        var stored = await LoadAsync(request.Id);

        Assert.NotNull(first);
        Assert.Equal(2, first.Version);
        Assert.Empty(first.StatusHistory);
        Assert.Null(second);
        Assert.Equal(2, stored.Version);
        Assert.Single(stored.StatusHistory);
    }

    [Fact]
    public async Task Summary_IsInvalidatedAfterSuccessfulUpdate()
    {
        var request = await CreateRequestAsync();
        var cache = _fixture.Factory.Services.GetRequiredService<IRequestSummaryCache>();
        cache.Invalidate();
        var before = await GetSummaryAsync();

        var response = await PutStatusAsync(request.Id.ToString(), RequestStatus.InProgress, 1);
        var after = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(before.GeneratedAt, after.GeneratedAt);
        Assert.Equal(Count(before, RequestStatus.New) - 1, Count(after, RequestStatus.New));
        Assert.Equal(Count(before, RequestStatus.InProgress) + 1, Count(after, RequestStatus.InProgress));
        Assert.Equal(before.TotalCount, after.TotalCount);
    }

    [Fact]
    public async Task Summary_IsNotInvalidatedByFailedUpdates()
    {
        var request = await CreateRequestAsync();
        await PutStatusAsync(request.Id.ToString(), RequestStatus.InProgress, 1);
        var cache = _fixture.Factory.Services.GetRequiredService<IRequestSummaryCache>();
        cache.Invalidate();
        var before = await GetSummaryAsync();

        var conflict = await PutStatusAsync(request.Id.ToString(), RequestStatus.Waiting, expectedVersion: 1);
        var invalid = await PutStatusAsync(request.Id.ToString(), RequestStatus.New, expectedVersion: 2);
        var missing = await PutStatusAsync(ObjectId.GenerateNewId().ToString(), RequestStatus.InProgress, 1);
        var malformed = await SendAsync(request.Id.ToString(), """{ "status": "InProgress" }""", ChangedBy);
        var after = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(before.GeneratedAt, after.GeneratedAt);
    }

    private async Task<RequestDocument> CreateRequestAsync()
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var request = new RequestDocument
        {
            Title = "Status update test request",
            OrganizationName = "Status Test Org",
            Status = RequestStatus.New,
            Priority = RequestPriority.Medium,
            AssignedTo = "status.test",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Version = 1
        };

        await _collection.InsertOneAsync(request);
        return request;
    }

    private async Task<RequestDocument> LoadAsync(ObjectId id) =>
        await _collection.Find(x => x.Id == id).SingleAsync();

    private Task<HttpResponseMessage> PutStatusAsync(string id, RequestStatus status, int expectedVersion) =>
        SendAsync(id, JsonContent.Create(new { status = status.ToString(), expectedVersion }), ChangedBy);

    private Task<HttpResponseMessage> SendAsync(string id, string json, string? changedBy) =>
        SendAsync(id, new StringContent(json, Encoding.UTF8, "application/json"), changedBy);

    private Task<HttpResponseMessage> SendAsync(string id, HttpContent content, string? changedBy)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status") { Content = content };
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
