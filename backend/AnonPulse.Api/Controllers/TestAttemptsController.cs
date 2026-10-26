using System.Security.Claims;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Tests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/polls/{pollId:guid}/test-attempts")]
[Authorize(Roles = RoleNames.Teacher + "," + RoleNames.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TestAttemptsController(ITestAttemptService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<TestAttemptResponse>>> Get(
        Guid pollId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AdminPagination.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > AdminPagination.MaximumPageSize)
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(page)] = ["Page must be positive."],
                [nameof(pageSize)] =
                    [$"PageSize must be between 1 and {AdminPagination.MaximumPageSize}."]
            }));
        }
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid actorId))
        {
            return Unauthorized();
        }
        TestAttemptsResult result = await service.GetAsync(
            actorId, User.IsInRole(RoleNames.Admin), pollId, page, pageSize,
            cancellationToken);
        return result.Outcome switch
        {
            TestAttemptsOutcome.Succeeded => Ok(result.Response),
            TestAttemptsOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Poll was not found."
            }),
            TestAttemptsOutcome.Conflict => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Test attempts are not available.",
                Detail = result.Error
            }),
            _ => throw new InvalidOperationException("Unknown test attempts outcome.")
        };
    }
}
