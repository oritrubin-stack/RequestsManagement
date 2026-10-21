using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Features.Requests.Dtos;

public sealed record StatusChangeDto(
    string RequestId,
    RequestStatus PreviousStatus,
    RequestStatus NewStatus,
    DateTime ChangedAt,
    string ChangedBy);
