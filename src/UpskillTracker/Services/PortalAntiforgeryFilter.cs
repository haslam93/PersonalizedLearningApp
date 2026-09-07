using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using UpskillTracker.Components.Authentication;

namespace UpskillTracker.Services;

public sealed class PortalAntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            if (context.HttpContext.Request.Path.StartsWithSegments("/auth"))
            {
                return new RazorComponentResult<AuthenticationPage>(new { ErrorCode = "request" })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }

            return Results.BadRequest(new { error = "The request could not be verified. Reload and try again." });
        }

        return await next(context);
    }
}
