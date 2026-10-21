using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

public static class RequestMappings
{
    public static RequestDto ToDto(this RequestDocument document) =>
        new(
            document.Id.ToString(),
            document.Title,
            document.OrganizationName,
            document.Status,
            document.Priority,
            document.AssignedTo,
            document.CreatedAt,
            document.UpdatedAt,
            document.Version);

    // The history is embedded in the request document, so the request id comes from the parent document.
    public static StatusChangeDto ToDto(this StatusChangeEntry entry, ObjectId requestId) =>
        new(
            requestId.ToString(),
            entry.PreviousStatus,
            entry.NewStatus,
            entry.ChangedAt,
            entry.ChangedBy);
}
