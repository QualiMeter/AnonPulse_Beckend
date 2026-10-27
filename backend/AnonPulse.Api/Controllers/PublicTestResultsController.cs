using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Services.Tests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnonPulse.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/public/polls/{pollId:guid}/test-attempts/{attemptId:guid}/result")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PublicTestResultsController(IPublicTestResultService service)
    : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting("public-test-results")]
    [RequestSizeLimit(1024)]
    public async Task<ActionResult<PublicTestResultResponse>> Get(
        Guid pollId,
        Guid attemptId,
        GetTestResultRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        PublicTestResultServiceResult result = await service.GetAsync(
            pollId, attemptId, request.AccessSecret, cancellationToken);
        return result.Outcome switch
        {
            PublicTestResultOutcome.Succeeded => Ok(result.Response),
            PublicTestResultOutcome.Invalid => BadRequest(new ValidationProblemDetails(
                result.ValidationErrors!.ToDictionary(item => item.Key, item => item.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred."
            }),
            PublicTestResultOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Test result was not found.",
                Extensions = { ["code"] = "test_result_not_found" }
            }),
            _ => throw new InvalidOperationException("Unknown test result outcome.")
        };
    }
}
