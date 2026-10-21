using System.Net;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;
using RequestsManagement.Api.Tests.Infrastructure;

namespace RequestsManagement.Api.Tests.Integration;

// Read-only: compares the history endpoint with the seeded documents.
[Collection(MongoIntegrationCollection.Name)]
public sealed class RequestsHistoryTests
{
    private readonly MongoTestDatabaseFixture _fixture;

    public RequestsHistoryTests(MongoTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task History_ForRequestWithChanges_MatchesStoredHistoryInOrder()
    {
        var expected = _fixture.Requests.First(x => x.StatusHistory.Count >= 3);

        var response = await _fixture.Client.GetAsync($"/api/requests/{expected.Id}/history");
        var history = await response.ReadAsAsync<List<StatusChangeDto>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            expected.StatusHistory.Select(x => (expected.Id.ToString(), x.PreviousStatus, x.NewStatus, x.ChangedAt, x.ChangedBy)),
            history.Select(x => (x.RequestId, x.PreviousStatus, x.NewStatus, x.ChangedAt, x.ChangedBy)));
    }

    [Fact]
    public async Task History_ForNewRequest_IsEmptyArray()
    {
        var newRequest = _fixture.Requests.First(x => x.Status == RequestStatus.New);

        var response = await _fixture.Client.GetAsync($"/api/requests/{newRequest.Id}/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_ForUnknownRequest_Returns404()
    {
        var response = await _fixture.Client.GetAsync($"/api/requests/{ObjectId.GenerateNewId()}/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task History_ForInvalidId_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/api/requests/not-an-id/history");
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("id", out _));
    }
}
