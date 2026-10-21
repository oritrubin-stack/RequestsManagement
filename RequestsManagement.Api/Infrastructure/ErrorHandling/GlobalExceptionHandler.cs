using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace RequestsManagement.Api.Infrastructure.ErrorHandling;

// Converts unhandled exceptions into ProblemDetails responses without exposing internal details to the client.
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    // Non-standard status code commonly used for requests the client closed before a response was sent.
    private const int ClientClosedRequest = 499;

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Request {Method} {Path} was cancelled by the client", httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        int statusCode;
        string title;

        // Server selection timeouts surface as TimeoutException; failures on an open connection as MongoConnectionException.
        if (exception is MongoConnectionException or TimeoutException)
        {
            statusCode = StatusCodes.Status503ServiceUnavailable;
            title = "The database is temporarily unavailable.";
            _logger.LogWarning(exception, "Database unavailable while handling {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            statusCode = StatusCodes.Status500InternalServerError;
            title = "An unexpected error occurred.";
            _logger.LogError(exception, "Unhandled exception while handling {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Extensions = { ["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier }
        };

        if (await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = problemDetails
            }))
        {
            return true;
        }

        // The service declines when the client does not accept JSON (for example, Accept: text/html).
        // The API still answers with the same ProblemDetails instead of falling back to a generic 500 body.
        await httpContext.Response.WriteAsJsonAsync(problemDetails, (JsonSerializerOptions?)null, "application/problem+json", cancellationToken);
        return true;
    }
}
