using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Features.Requests.Dtos;

public sealed record RequestDto(
    string Id,
    string Title,
    string OrganizationName,
    RequestStatus Status,
    RequestPriority Priority,
    string AssignedTo,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Version);
