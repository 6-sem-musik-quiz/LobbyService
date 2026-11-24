using LobbyService.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace LobbyService.Api.Middleware;

// Maps domain exceptions to 400/404/409 and anything else to a ProblemDetails 500 response.
public class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            LobbyValidationException e => (StatusCodes.Status400BadRequest, "Invalid request.", e.Message),
            LobbyConflictException e => (StatusCodes.Status409Conflict, "Conflict.", e.Message),
            LobbyNotFoundException e => (StatusCodes.Status404NotFound, "Not found.", e.Message),
            LobbyCodeNotFoundException e => (StatusCodes.Status404NotFound, "Not found.", e.Message),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", null)
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Title = title,
                Detail = detail,
                Status = status
            }
        });
    }
}
