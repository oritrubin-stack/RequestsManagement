namespace RequestsManagement.Api.Features.Requests.Dtos;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages)
{
    public static PagedResult<T> Create(IReadOnlyList<T> items, int page, int pageSize, long totalCount) =>
        new(items, page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize));
}
