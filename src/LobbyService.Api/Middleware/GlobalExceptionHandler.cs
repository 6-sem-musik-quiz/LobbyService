using LobbyService.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace LobbyService.Api.Middleware;

// Maps domain exceptions to 400/403/404/409/502 and anything else to a ProblemDetails 500 response.
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
            LobbyForbiddenException e => (StatusCodes.Status403Forbidden, "Forbidden.", e.Message),
            GameServiceUnavailableException e => (StatusCodes.Status502BadGateway, "Bad gateway.", e.Message),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", null)
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        else if (status == StatusCodes.Status502BadGateway)
            logger.LogWarning(exception, "Downstream call failed for {Method} {Path}",
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
