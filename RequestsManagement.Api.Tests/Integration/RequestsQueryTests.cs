using System.Net;
using System.Text.RegularExpressions;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;
using RequestsManagement.Api.Tests.Infrastructure;

namespace RequestsManagement.Api.Tests.Integration;

// End-to-end tests through HTTP, the real API pipeline and a real MongoDB database.
// Expected results are computed in memory from the same seeded documents.
[Collection(MongoIntegrationCollection.Name)]
public sealed class RequestsQueryTests
{
    private readonly MongoTestDatabaseFixture _fixture;

    public RequestsQueryTests(MongoTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Search_WithoutParameters_ReturnsFirstPageNewestFirst()
    {
        var page = await GetPageAsync("/api/requests");

        var expected = _fixture.Requests
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(RequestQueryParameters.DefaultPageSize)
            .Select(x => x.Id.ToString());

        Assert.Equal(expected, page.Items.Select(x => x.Id));
        Assert.Equal(MongoTestDatabaseFixture.RequestCount, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(25, page.TotalPages);
    }

    [Fact]
    public async Task Search_WithCombinedFilters_ReturnsOnlyMatchingRequests()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var page = await GetPageAsync(
            "/api/requests?status=Completed&priority=Medium&createdFrom=2025-01-01T00:00:00Z&createdTo=2026-01-01T00:00:00Z&pageSize=100");

        var expected = _fixture.Requests
            .Where(x => x.Status == RequestStatus.Completed && x.Priority == RequestPriority.Medium)
            .Where(x => x.CreatedAt >= from && x.CreatedAt < to)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => x.Id.ToString())
            .ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, page.Items.Select(x => x.Id));
        Assert.Equal(expected.Count, page.TotalCount);
    }

    [Fact]
    public async Task Search_WithDateRange_IsHalfOpenAndTreatsDatesWithoutOffsetAsUtc()
    {
        var target = _fixture.Requests[0];
        var exact = target.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        var narrow = $"organizationName={Uri.EscapeDataString(target.OrganizationName)}&assignedTo={Uri.EscapeDataString(target.AssignedTo)}&pageSize=100";

        var fromIncludes = await GetPageAsync($"/api/requests?{narrow}&createdFrom={exact}");
        var toExcludes = await GetPageAsync($"/api/requests?{narrow}&createdTo={exact}");

        Assert.Contains(target.Id.ToString(), fromIncludes.Items.Select(x => x.Id));
        Assert.DoesNotContain(target.Id.ToString(), toExcludes.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task Search_SortedByPriorityDescending_ReturnsHighestPriorityOldestFirst()
    {
        var page = await GetPageAsync("/api/requests?sortBy=priority&sortDirection=desc&pageSize=100");

        var expected = _fixture.Requests
            .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Take(100)
            .Select(x => x.Id.ToString());

        Assert.Equal(expected, page.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task Search_SortedByUpdatedAtAscending_ReturnsOldestUpdateFirst()
    {
        var page = await GetPageAsync("/api/requests?sortBy=updatedAt&sortDirection=asc&pageSize=100");

        var expected = _fixture.Requests
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Take(100)
            .Select(x => x.Id.ToString());

        Assert.Equal(expected, page.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task Search_AllPages_ReturnEveryMatchingRequestExactlyOnceInOrder()
    {
        var expected = _fixture.Requests
            .Where(x => x.Status == RequestStatus.Completed)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => x.Id.ToString())
            .ToList();

        var collected = new List<string>();
        PagedResult<RequestDto> page;
        var pageNumber = 1;
        do
        {
            page = await GetPageAsync($"/api/requests?status=Completed&pageSize=50&page={pageNumber++}");
            collected.AddRange(page.Items.Select(x => x.Id));
        }
        while (page.Page < page.TotalPages);

        Assert.Equal(expected, collected);
    }

    [Fact]
    public async Task Search_PageBeyondLastPage_ReturnsEmptyItems()
    {
        var page = await GetPageAsync("/api/requests?page=1000&pageSize=100");

        Assert.Empty(page.Items);
        Assert.Equal(MongoTestDatabaseFixture.RequestCount, page.TotalCount);
    }

    [Fact]
    public async Task Search_WithText_MatchesWholeWordsInTitle()
    {
        var page = await GetPageAsync("/api/requests?search=invoice&pageSize=100");

        var wholeWord = new Regex(@"\binvoice\b", RegexOptions.IgnoreCase);
        var expected = _fixture.Requests
            .Where(x => wholeWord.IsMatch(x.Title) || wholeWord.IsMatch(x.OrganizationName))
            .Select(x => x.Id.ToString())
            .ToHashSet();

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, page.TotalCount);
        Assert.Equal(expected, page.Items.Select(x => x.Id).ToHashSet());
    }

    [Fact]
    public async Task Search_WithPartialWord_ReturnsNoResults()
    {
        var page = await GetPageAsync("/api/requests?search=invoic");

        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Search_ByOrganizationName_IsExactAndCaseSensitive()
    {
        var organization = _fixture.Requests[0].OrganizationName;
        var expectedCount = _fixture.Requests.Count(x => x.OrganizationName == organization);

        var exact = await GetPageAsync($"/api/requests?organizationName={Uri.EscapeDataString(organization)}");
        var lowerCase = await GetPageAsync($"/api/requests?organizationName={Uri.EscapeDataString(organization.ToLowerInvariant())}");

        Assert.Equal(expectedCount, exact.TotalCount);
        Assert.Equal(0, lowerCase.TotalCount);
    }

    [Fact]
    public async Task GetById_ExistingRequest_ReturnsRequest()
    {
        var expected = _fixture.Requests[0];

        var response = await _fixture.Client.GetAsync($"/api/requests/{expected.Id}");
        var request = await response.ReadAsAsync<RequestDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected.Id.ToString(), request.Id);
        Assert.Equal(expected.Title, request.Title);
        Assert.Equal(expected.Status, request.Status);
        Assert.Equal(expected.Version, request.Version);
        Assert.Equal(expected.CreatedAt, request.CreatedAt);
    }

    [Fact]
    public async Task GetById_UnknownRequest_Returns404ProblemDetails()
    {
        var response = await _fixture.Client.GetAsync("/api/requests/000000000000000000000000");
        var problem = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    private async Task<PagedResult<RequestDto>> GetPageAsync(string url)
    {
        var response = await _fixture.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsAsync<PagedResult<RequestDto>>();
    }
}
