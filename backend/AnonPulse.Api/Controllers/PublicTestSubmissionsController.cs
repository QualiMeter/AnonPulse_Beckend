using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Services.Tests;
using AnonPulse.Api.Services.Voting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnonPulse.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/public/polls/{pollId:guid}/test-submissions")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PublicTestSubmissionsController(ITestSubmissionService service)
    : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting("public-test-submissions")]
    [TextAnswerRequestSizeLimit]
    public async Task<ActionResult<TestSubmissionCreatedResponse>> Submit(
        Guid pollId,
        SubmitTestRequest request,
        CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("X-AnonPulse-Test", out var header) ||
            header.Count != 1 || header[0] != "1")
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["X-AnonPulse-Test"] = ["The X-AnonPulse-Test: 1 header is required."]
            }));
        }
        TestSubmissionResult result = await service.SubmitAsync(
            pollId, request, cancellationToken);
        return result.Outcome switch
        {
            TestSubmissionOutcome.Succeeded => StatusCode(
                StatusCodes.Status201Created, result.Response),
            TestSubmissionOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Poll was not found."
            }),
            TestSubmissionOutcome.Invalid => BadRequest(new ValidationProblemDetails(
                result.ValidationErrors!.ToDictionary(item => item.Key, item => item.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred."
            }),
            TestSubmissionOutcome.Conflict => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Test submission was not accepted.",
                Detail = result.Error,
                Extensions = { ["code"] = result.Code }
            }),
            _ => throw new InvalidOperationException("Unknown test submission outcome.")
        };
    }
}
