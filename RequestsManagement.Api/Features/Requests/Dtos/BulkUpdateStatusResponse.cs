using System.Text.Json.Serialization;
using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Features.Requests.Dtos;

// Results are in the same order as the request items.
public sealed record BulkUpdateStatusResponse(
    int UpdatedCount,
    int NotFoundCount,
    int VersionConflictCount,
    int InvalidTransitionCount,
    IReadOnlyList<BulkUpdateStatusItemResult> Results);

public sealed record BulkUpdateStatusItemResult(
    string Id,
    StatusUpdateOutcome Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RequestDto? Request = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CurrentVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RequestStatus? CurrentStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);
