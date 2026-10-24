using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AnonPulse.Api.Security;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class ValidateCsrfTokenAttribute : TypeFilterAttribute
{
    public ValidateCsrfTokenAttribute() : base(typeof(ValidateCsrfTokenFilter))
    {
    }
}

public sealed class ValidateCsrfTokenFilter(IAntiforgery antiforgery)
    : IAsyncAuthorizationFilter, IOrderedFilter
{
    public int Order => 1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "CSRF validation failed.",
                Detail = "Obtain a CSRF token and send it in the X-CSRF-TOKEN header.",
                Extensions = { ["code"] = "csrf_validation_failed" }
            });
        }
    }
}
