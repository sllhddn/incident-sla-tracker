using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SlaTracker.Api.Domain;
using SlaTracker.Api.Endpoints;

namespace SlaTracker.Api.Infrastructure;

/// <summary>Turns known exceptions into RFC 7807 problem details with the right status code.</summary>
public sealed class ErrorHandler(IProblemDetailsService problems, ILogger<ErrorHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        ProblemDetails pd = ex switch
        {
            RequestValidationException v => new ValidationProblemDetails(v.Errors) { Status = 400, Title = "Validation failed" },
            NotFoundException => new ProblemDetails { Status = 404, Title = "Not found", Detail = ex.Message },
            DomainException => new ProblemDetails { Status = 409, Title = "Not allowed", Detail = ex.Message },
            DbUpdateConcurrencyException => new ProblemDetails
            {
                Status = 409, Title = "Edited by someone else",
                Detail = "The incident changed after you loaded it. Reload and try again.",
            },
            BadHttpRequestException b => new ProblemDetails { Status = 400, Title = "Bad request", Detail = b.Message },
            _ => new ProblemDetails { Status = 500, Title = "Unexpected error" },
        };
        if (pd.Status == 500) log.LogError(ex, "Unhandled error on {Path}", ctx.Request.Path);

        ctx.Response.StatusCode = pd.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = ctx, ProblemDetails = pd, Exception = ex });
    }
}
