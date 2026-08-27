using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// Turns an upstream Atlassian failure into a ProblemDetails the UI can show,
/// rather than a 500 that says nothing.
/// </summary>
public sealed class AtlassianExceptionHandler(ILogger<AtlassianExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not AtlassianApiException atlassian)
        {
            return false;
        }

        logger.LogWarning(
            "{Tenant} tenant returned {StatusCode} for {Path}",
            atlassian.Tenant, (int)atlassian.StatusCode, atlassian.Path);

        // 4xx from Atlassian is a problem with what we asked for and is worth
        // relaying verbatim. 5xx from Atlassian is not OUR server failing, so
        // it surfaces as 502 rather than 500.
        var status = (int)atlassian.StatusCode;
        var responseStatus = status is >= 400 and < 500 ? status : StatusCodes.Status502BadGateway;

        var problem = new ProblemDetails
        {
            Title = $"Atlassian {atlassian.Tenant.ToString().ToLowerInvariant()} tenant returned {status}",
            Detail = atlassian.ResponseBody,
            Status = responseStatus,
        };

        if (atlassian.IsRateLimit)
        {
            problem.Title = "Atlassian rate limit reached";
            problem.Detail = "The tenant returned 429. Retry after the interval it asked for.";
        }

        context.Response.StatusCode = responseStatus;
        await context.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
