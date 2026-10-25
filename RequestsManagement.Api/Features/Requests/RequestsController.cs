using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Features.Requests;

// No [Produces] attribute: it would force "application/json" on every result,
// overriding the "application/problem+json" content type of 400 and 404 responses.
[ApiController]
[Route("api/requests")]
public sealed class RequestsController : ControllerBase
{
    public const string ChangedByHeader = "X-User-Name";

    private const string ChangedByPattern = "^[A-Za-z0-9._@-]+$";
    private const string ChangedByRequiredMessage = "The X-User-Name header is required.";
    private const string ChangedByLengthMessage = "X-User-Name must be at most 100 characters.";
    private const string ChangedByPatternMessage = "X-User-Name may contain only letters, digits and . _ @ -";

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
        if (!TryParseId(id, out var objectId))
        {
            return ValidationProblem(ModelState);
        }

        var request = await _service.GetByIdAsync(objectId, cancellationToken);
        return request is null ? NotFound() : Ok(request);
    }

    // Optimistic concurrency: the update succeeds only if expectedVersion is still the current version (409 otherwise).
    // X-User-Name identifies who made the change for the audit history. It is declared by the client and
    // not verified: the project has no authentication.
    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(RequestDto), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<RequestDto>> UpdateStatus(
        string id,
        [FromBody] UpdateStatusRequest request,
        [FromHeader(Name = ChangedByHeader)]
        [Required(ErrorMessage = ChangedByRequiredMessage)]
        [StringLength(100, ErrorMessage = ChangedByLengthMessage)]
        [RegularExpression(ChangedByPattern, ErrorMessage = ChangedByPatternMessage)]
        string changedBy,
        CancellationToken cancellationToken)
    {
        if (!TryParseId(id, out var objectId))
        {
            return ValidationProblem(ModelState);
        }

        var newStatus = request.Status!.Value;
        var result = await _service.UpdateStatusAsync(
            objectId,
            newStatus,
            request.ExpectedVersion!.Value,
            changedBy,
            cancellationToken);

        switch (result.Outcome)
        {
            case StatusUpdateOutcome.Updated:
                return Ok(result.Request);

            case StatusUpdateOutcome.NotFound:
                return NotFound();

            case StatusUpdateOutcome.InvalidTransition:
                ModelState.AddModelError(
                    nameof(UpdateStatusRequest.Status),
                    DescribeInvalidTransition(result.CurrentStatus!.Value, newStatus));
                return ValidationProblem(ModelState);

            default:
                var problem = ProblemDetailsFactory.CreateProblemDetails(
                    HttpContext,
                    StatusCodes.Status409Conflict,
                    title: "The request was changed by another update.",
                    detail: "Reload the request and apply the change to its current version.");
                problem.Extensions["currentVersion"] = result.CurrentVersion;
                problem.Extensions["currentStatus"] = result.CurrentStatus?.ToString();

                return new ObjectResult(problem)
                {
                    StatusCode = StatusCodes.Status409Conflict,
                    ContentTypes = { "application/problem+json" }
                };
        }
    }

    // Partial success: each item is applied on its own with the same rules as PUT /{id}/status, and gets its own result
    // (in request order). A structurally invalid request (more than 100 items, invalid or duplicate ids, invalid fields)
    // is rejected with 400 before any item is processed. If the database fails mid-batch, processing stops and the
    // error propagates; items applied before it stay applied (see README).
    [HttpPost("status/bulk")]
    [ProducesResponseType(typeof(BulkUpdateStatusResponse), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<BulkUpdateStatusResponse>> BulkUpdateStatus(
        [FromBody] BulkUpdateStatusRequest request,
        [FromHeader(Name = ChangedByHeader)]
        [Required(ErrorMessage = ChangedByRequiredMessage)]
        [StringLength(100, ErrorMessage = ChangedByLengthMessage)]
        [RegularExpression(ChangedByPattern, ErrorMessage = ChangedByPatternMessage)]
        string changedBy,
        CancellationToken cancellationToken)
    {
        // Model validation has already checked every item, so the ids parse and the values are present.
        var changes = request.Items!
            .Select(item => new BulkStatusChange(ObjectId.Parse(item.Id), item.Status!.Value, item.ExpectedVersion!.Value))
            .ToList();

        var results = await _service.BulkUpdateStatusAsync(changes, changedBy, cancellationToken);

        return Ok(new BulkUpdateStatusResponse(
            results.Count(x => x.Result.Outcome == StatusUpdateOutcome.Updated),
            results.Count(x => x.Result.Outcome == StatusUpdateOutcome.NotFound),
            results.Count(x => x.Result.Outcome == StatusUpdateOutcome.VersionConflict),
            results.Count(x => x.Result.Outcome == StatusUpdateOutcome.InvalidTransition),
            results.Select(ToItemResult).ToList()));
    }

    // Returns the status changes in chronological order; an empty array when the request has none.
    [HttpGet("{id}/history")]
    [ProducesResponseType(typeof(IReadOnlyList<StatusChangeDto>), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<IReadOnlyList<StatusChangeDto>>> GetHistory(string id, CancellationToken cancellationToken)
    {
        if (!TryParseId(id, out var objectId))
        {
            return ValidationProblem(ModelState);
        }

        var history = await _service.GetHistoryAsync(objectId, cancellationToken);
        return history is null ? NotFound() : Ok(history);
    }

    private bool TryParseId(string id, out ObjectId objectId)
    {
        if (ObjectId.TryParse(id, out objectId))
        {
            return true;
        }

        ModelState.AddModelError(nameof(id), "The id must be a 24-character hexadecimal string.");
        return false;
    }

    private static BulkUpdateStatusItemResult ToItemResult(BulkStatusChangeResult item)
    {
        var id = item.Change.Id.ToString();
        var result = item.Result;

        return result.Outcome switch
        {
            StatusUpdateOutcome.Updated => new BulkUpdateStatusItemResult(id, result.Outcome, Request: result.Request),
            StatusUpdateOutcome.NotFound => new BulkUpdateStatusItemResult(id, result.Outcome),
            StatusUpdateOutcome.InvalidTransition => new BulkUpdateStatusItemResult(
                id,
                result.Outcome,
                CurrentVersion: result.CurrentVersion,
                CurrentStatus: result.CurrentStatus,
                Message: DescribeInvalidTransition(result.CurrentStatus!.Value, item.Change.NewStatus)),
            _ => new BulkUpdateStatusItemResult(
                id,
                result.Outcome,
                CurrentVersion: result.CurrentVersion,
                CurrentStatus: result.CurrentStatus)
        };
    }

    private static string DescribeInvalidTransition(RequestStatus currentStatus, RequestStatus newStatus)
    {
        if (currentStatus == newStatus)
        {
            return $"The request is already in status {currentStatus}.";
        }

        var allowed = StatusTransitions.GetAllowedTargets(currentStatus);
        return allowed.Count == 0
            ? $"The request is in status {currentStatus}, which is final."
            : $"Transition from {currentStatus} to {newStatus} is not allowed. Allowed: {string.Join(", ", allowed)}.";
    }
}
