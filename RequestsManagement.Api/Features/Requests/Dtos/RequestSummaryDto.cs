using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Features.Requests.Dtos;

// Every status and priority is always present (zero when absent), in enum order.
// TotalCount is the sum of ByStatus.
public sealed record RequestSummaryDto(
    long TotalCount,
    IReadOnlyList<StatusCountDto> ByStatus,
    IReadOnlyList<PriorityCountDto> ByPriority,
    DateTime GeneratedAt);

public sealed record StatusCountDto(RequestStatus Status, long Count);

public sealed record PriorityCountDto(RequestPriority Priority, long Count);
