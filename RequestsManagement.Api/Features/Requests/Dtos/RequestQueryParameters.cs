using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Features.Requests.Dtos;

public sealed class RequestQueryParameters : IValidatableObject
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    // Keeps (page - 1) * pageSize within the range of int, which is what MongoDB's skip accepts.
    public const int MaxPage = 1_000_000;

    // Free-text search on whole words in Title and OrganizationName (MongoDB $text semantics).
    [StringLength(100)]
    public string? Search { get; init; }

    // EnumDataType rejects numeric values that are not defined by the enum, which model binding would otherwise accept.
    [EnumDataType(typeof(RequestStatus))]
    public RequestStatus? Status { get; init; }

    [EnumDataType(typeof(RequestPriority))]
    public RequestPriority? Priority { get; init; }

    // Exact, case-sensitive match.
    [StringLength(200)]
    public string? OrganizationName { get; init; }

    // Exact, case-sensitive match.
    [StringLength(200)]
    public string? AssignedTo { get; init; }

    // Inclusive lower bound of CreatedAt. Values without an offset are treated as UTC.
    public DateTime? CreatedFrom { get; init; }

    // Exclusive upper bound of CreatedAt. Values without an offset are treated as UTC.
    public DateTime? CreatedTo { get; init; }

    [EnumDataType(typeof(RequestSortField))]
    public RequestSortField SortBy { get; init; } = RequestSortField.CreatedAt;

    [EnumDataType(typeof(SortDirection))]
    public SortDirection SortDirection { get; init; } = SortDirection.Desc;

    [Range(1, MaxPage)]
    public int Page { get; init; } = 1;

    [Range(1, MaxPageSize)]
    public int PageSize { get; init; } = DefaultPageSize;

    // Computed values, not query parameters; BindNever also keeps them out of the OpenAPI description.
    [BindNever]
    public DateTime? CreatedFromUtc => ToUtc(CreatedFrom);

    [BindNever]
    public DateTime? CreatedToUtc => ToUtc(CreatedTo);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CreatedFromUtc >= CreatedToUtc)
        {
            yield return new ValidationResult(
                "CreatedFrom must be earlier than CreatedTo.",
                [nameof(CreatedFrom), nameof(CreatedTo)]);
        }
    }

    // Model binding returns Local for values with an offset and Unspecified for values without one,
    // and the MongoDB driver treats Unspecified as local time. Normalizing here keeps all comparisons in UTC.
    private static DateTime? ToUtc(DateTime? value) => value is not { } dateTime
        ? null
        : dateTime.Kind switch
        {
            DateTimeKind.Utc => dateTime,
            DateTimeKind.Local => dateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
        };
}
