using System.Security.Claims;
using AnonPulse.Api.Contracts.PollResults;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.PollResults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/polls")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PollResultsController(IPollResultsService pollResultsService)
    : ControllerBase
{
    [HttpGet("{id:guid}/results")]
    public async Task<ActionResult<PollResultsResponse>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        Guid? userId = Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out Guid parsedUserId)
            ? parsedUserId
            : null;
        var accessContext = new PollResultsAccessContext(
            userId,
            User.Identity?.IsAuthenticated == true,
            User.IsInRole(RoleNames.Teacher),
            User.IsInRole(RoleNames.Admin));
        PollResultsResult result =
            await pollResultsService.GetAsync(id, accessContext, cancellationToken);

        return result.Outcome switch
        {
            PollResultsOutcome.Succeeded => Ok(result.Results),
            PollResultsOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Poll was not found."
            }),
            PollResultsOutcome.NotPublished => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Poll results are not available.",
                Detail = "Results are available only after the poll is published."
            }),
            PollResultsOutcome.NotAvailableForTest => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Survey aggregate results are not available for tests.",
                Detail = "Use the protected test-attempts endpoint after the test is closed."
            }),
            PollResultsOutcome.Unauthorized => Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Authentication is required.",
                detail: "Interim results are available only to the poll owner or an administrator."),
            PollResultsOutcome.Forbidden => Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Access to interim results is forbidden.",
                detail: "The current user does not have permission to view interim results."),
            _ => throw new InvalidOperationException(
                $"Unknown poll results outcome: {result.Outcome}.")
        };
    }
}
