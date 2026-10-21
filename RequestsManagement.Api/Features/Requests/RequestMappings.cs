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
}
