using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.Voting;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TextAnswerRequestSizeLimitAttribute : Attribute, IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        TextAnswerOptions options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<TextAnswerOptions>>().Value;
        IHttpMaxRequestBodySizeFeature? feature = context.HttpContext.Features
            .Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = options.MaximumRequestBodySize;
        }

        if (context.HttpContext.Request.ContentLength is long contentLength &&
            contentLength > options.MaximumRequestBodySize)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Vote submission is too large.",
                Detail = $"The request body must not exceed {options.MaximumRequestBodySize} bytes."
            })
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge,
                ContentTypes = { "application/problem+json" }
            };
            return Task.CompletedTask;
        }

        return ContinueAsync(next);
    }

    private static async Task ContinueAsync(ResourceExecutionDelegate next) =>
        await next();
}
