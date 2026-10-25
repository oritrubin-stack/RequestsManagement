using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Features.Requests.Dtos;

// Every structural problem (item count, id format, duplicate ids, invalid item fields) is reported by model
// validation as a 400 before any item is processed, so an invalid request never applies partial updates.
public sealed class BulkUpdateStatusRequest : IValidatableObject
{
    public const int MaxItems = 100;

    [Required]
    [MinLength(1)]
    [MaxLength(MaxItems)]
    public List<BulkUpdateStatusItem>? Items { get; init; }

    // Runs after the item attributes are valid. Duplicates are rejected because two changes to the same request
    // in one batch have no clear meaning; ids are compared as ObjectIds, so letter case does not matter.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Items is null)
        {
            yield break;
        }

        var seen = new HashSet<ObjectId>();
        var duplicates = new List<string>();

        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item is null)
            {
                yield return new ValidationResult("The item is required.", [$"{nameof(Items)}[{i}]"]);
                continue;
            }

            if (!ObjectId.TryParse(item.Id, out var id))
            {
                yield return new ValidationResult(
                    "The id must be a 24-character hexadecimal string.",
                    [$"{nameof(Items)}[{i}].{nameof(BulkUpdateStatusItem.Id)}"]);
                continue;
            }

            if (!seen.Add(id) && !duplicates.Contains(id.ToString()))
            {
                duplicates.Add(id.ToString());
            }
        }

        if (duplicates.Count > 0)
        {
            yield return new ValidationResult(
                $"Duplicate request ids: {string.Join(", ", duplicates)}.",
                [nameof(Items)]);
        }
    }
}

public sealed class BulkUpdateStatusItem
{
    [Required]
    public string? Id { get; init; }

    [Required]
    [EnumDataType(typeof(RequestStatus))]
    public RequestStatus? Status { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? ExpectedVersion { get; init; }
}
