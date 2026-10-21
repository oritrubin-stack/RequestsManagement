using System.ComponentModel.DataAnnotations;
using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Features.Requests.Dtos;

// Both values are nullable so that a missing value is reported as a validation error instead of defaulting.
public sealed class UpdateStatusRequest
{
    [Required]
    [EnumDataType(typeof(RequestStatus))]
    public RequestStatus? Status { get; init; }

    // The version the client last read; the update succeeds only if it is still the current version.
    [Required]
    [Range(1, int.MaxValue)]
    public int? ExpectedVersion { get; init; }
}
