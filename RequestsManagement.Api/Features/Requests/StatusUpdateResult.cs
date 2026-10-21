using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

public enum StatusUpdateOutcome
{
    Updated,
    NotFound,
    VersionConflict,
    InvalidTransition
}

// The outcome of one status update. A result value (rather than exceptions) lets the bulk update
// report an outcome per item without throwing for every failed item.
public sealed record StatusUpdateResult(
    StatusUpdateOutcome Outcome,
    RequestDto? Request = null,
    int? CurrentVersion = null,
    RequestStatus? CurrentStatus = null)
{
    public static StatusUpdateResult Updated(RequestDto request) =>
        new(StatusUpdateOutcome.Updated, request, request.Version, request.Status);

    public static StatusUpdateResult NotFound() =>
        new(StatusUpdateOutcome.NotFound);

    public static StatusUpdateResult VersionConflict(int currentVersion, RequestStatus currentStatus) =>
        new(StatusUpdateOutcome.VersionConflict, CurrentVersion: currentVersion, CurrentStatus: currentStatus);

    public static StatusUpdateResult InvalidTransition(int currentVersion, RequestStatus currentStatus) =>
        new(StatusUpdateOutcome.InvalidTransition, CurrentVersion: currentVersion, CurrentStatus: currentStatus);
}
