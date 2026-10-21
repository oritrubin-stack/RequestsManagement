using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Tests.Unit;

public sealed class RequestServiceSummaryTests
{
    [Fact]
    public async Task GetSummary_FillsMissingValuesWithZero_AndTotalIsSumOfStatuses()
    {
        var counts = new RequestSummaryCounts(
            new Dictionary<RequestStatus, long> { [RequestStatus.Waiting] = 3, [RequestStatus.Completed] = 2 },
            new Dictionary<RequestPriority, long> { [RequestPriority.High] = 5 });

        var summary = await CreateService(counts).GetSummaryAsync(CancellationToken.None);

        Assert.Equal(
            [(RequestStatus.New, 0L), (RequestStatus.InProgress, 0L), (RequestStatus.Waiting, 3L), (RequestStatus.Completed, 2L)],
            summary.ByStatus.Select(x => (x.Status, x.Count)));
        Assert.Equal(
            [(RequestPriority.Low, 0L), (RequestPriority.Medium, 0L), (RequestPriority.High, 5L)],
            summary.ByPriority.Select(x => (x.Priority, x.Count)));
        Assert.Equal(5, summary.TotalCount);
        Assert.Equal(DateTimeKind.Utc, summary.GeneratedAt.Kind);
    }

    [Fact]
    public async Task GetSummary_ForEmptyCollection_ReturnsAllZeros()
    {
        var counts = new RequestSummaryCounts(
            new Dictionary<RequestStatus, long>(),
            new Dictionary<RequestPriority, long>());

        var summary = await CreateService(counts).GetSummaryAsync(CancellationToken.None);

        Assert.Equal(0, summary.TotalCount);
        Assert.Equal(Enum.GetValues<RequestStatus>(), summary.ByStatus.Select(x => x.Status));
        Assert.Equal(Enum.GetValues<RequestPriority>(), summary.ByPriority.Select(x => x.Priority));
        Assert.All(summary.ByStatus, x => Assert.Equal(0, x.Count));
        Assert.All(summary.ByPriority, x => Assert.Equal(0, x.Count));
    }

    private static RequestService CreateService(RequestSummaryCounts counts)
    {
        var cache = new RequestSummaryCache(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new RequestSummaryCacheOptions()),
            NullLogger<RequestSummaryCache>.Instance);

        return new RequestService(new SummaryOnlyRepository(counts), cache, NullLogger<RequestService>.Instance);
    }

    private sealed class SummaryOnlyRepository : IRequestRepository
    {
        private readonly RequestSummaryCounts _counts;

        public SummaryOnlyRepository(RequestSummaryCounts counts)
        {
            _counts = counts;
        }

        public Task<RequestSummaryCounts> GetSummaryAsync(CancellationToken cancellationToken) => Task.FromResult(_counts);

        public Task<RequestSearchResult> SearchAsync(RequestQueryParameters query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RequestDocument?> GetByIdAsync(ObjectId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
