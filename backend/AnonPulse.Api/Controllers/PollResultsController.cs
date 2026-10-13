using AnonPulse.Api.Contracts.PollResults;
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
        PollResultsResult result =
            await pollResultsService.GetAsync(id, cancellationToken);

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
            _ => throw new InvalidOperationException(
                $"Unknown poll results outcome: {result.Outcome}.")
        };
    }
}
