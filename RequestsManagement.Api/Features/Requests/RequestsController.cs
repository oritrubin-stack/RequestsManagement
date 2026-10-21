using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

// No [Produces] attribute: it would force "application/json" on every result,
// overriding the "application/problem+json" content type of 400 and 404 responses.
[ApiController]
[Route("api/requests")]
public sealed class RequestsController : ControllerBase
{
    private readonly RequestService _service;

    public RequestsController(RequestService service)
    {
        _service = service;
    }

    // Invalid query parameters are rejected by [ApiController] with a 400 ValidationProblemDetails response.
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<RequestDto>), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<PagedResult<RequestDto>>> Search(
        [FromQuery] RequestQueryParameters query,
        CancellationToken cancellationToken)
    {
        return Ok(await _service.SearchAsync(query, cancellationToken));
    }

    // Counts by status and by priority across all requests. Served from a per-instance cache (see README).
    [HttpGet("summary")]
    [ProducesResponseType(typeof(RequestSummaryDto), StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<RequestSummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetSummaryAsync(cancellationToken));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RequestDto), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<RequestDto>> GetById(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out var objectId))
        {
            ModelState.AddModelError(nameof(id), "The id must be a 24-character hexadecimal string.");
            return ValidationProblem(ModelState);
        }

        var request = await _service.GetByIdAsync(objectId, cancellationToken);
        return request is null ? NotFound() : Ok(request);
    }
}
