using System.Net;
using RequestsManagement.Api.Tests.Infrastructure;

namespace RequestsManagement.Api.Tests.Integration;

[Collection(MongoIntegrationCollection.Name)]
public sealed class RequestsValidationTests
{
    private readonly MongoTestDatabaseFixture _fixture;

    public RequestsValidationTests(MongoTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("pageSize=0", "PageSize")]
    [InlineData("pageSize=101", "PageSize")]
    [InlineData("page=0", "Page")]
    [InlineData("page=1000001", "Page")]
    [InlineData("sortBy=title", "SortBy")]
    [InlineData("sortDirection=up", "SortDirection")]
    [InlineData("status=Closed", "Status")]
    [InlineData("status=7", "Status")]
    [InlineData("priority=9", "Priority")]
    [InlineData("createdFrom=not-a-date", "CreatedFrom")]
    [InlineData("createdFrom=2025-07-01&createdTo=2025-01-01", "CreatedFrom")]
    [InlineData("createdFrom=2025-01-01&createdTo=2025-01-01", "CreatedFrom")]
    public async Task Search_WithInvalidParameter_Returns400WithFieldError(string query, string expectedErrorKey)
    {
        var response = await _fixture.Client.GetAsync($"/api/requests?{query}");
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(problem.GetProperty("errors").TryGetProperty(expectedErrorKey, out _), $"Missing error for '{expectedErrorKey}'.");
    }

    [Fact]
    public async Task Search_WithTooLongSearchText_Returns400()
    {
        var response = await _fixture.Client.GetAsync($"/api/requests?search={new string('x', 101)}");
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("Search", out _));
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("12345")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task GetById_WithInvalidId_Returns400(string id)
    {
        var response = await _fixture.Client.GetAsync($"/api/requests/{id}");
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("id", out _));
    }
}
