using System.Security.Claims;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Polls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/polls/{pollId:guid}/questions/{questionId:guid}/text-answers")]
[Authorize(Roles = RoleNames.Teacher + "," + RoleNames.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PollTextAnswersController(IPollTextAnswerService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<PollTextAnswerResponse>>> Get(
        Guid pollId, Guid questionId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = AdminPagination.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > AdminPagination.MaximumPageSize)
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(page)] = ["Page must be positive."],
                [nameof(pageSize)] = [$"PageSize must be between 1 and {AdminPagination.MaximumPageSize}."]
            })
            { Status = 400, Title = "One or more validation errors occurred." });
        }
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid actorId))
        {
            return Unauthorized();
        }
        PollTextAnswersResult result = await service.GetAsync(actorId,
            User.IsInRole(RoleNames.Admin), pollId, questionId, page, pageSize,
            cancellationToken);
        return result.Outcome switch
        {
            PollTextAnswersOutcome.Succeeded => Ok(result.Response),
            PollTextAnswersOutcome.NotFound => NotFound(new ProblemDetails
            { Status = 404, Title = "Poll or text question was not found." }),
            PollTextAnswersOutcome.Conflict => Conflict(new ProblemDetails
            { Status = 409, Title = "Text answers are not available.", Detail = result.Error }),
            _ => throw new InvalidOperationException()
        };
    }
}
