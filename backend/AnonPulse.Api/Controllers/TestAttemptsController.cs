using System.Security.Claims;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Models;
using AnonPulse.Api.Security;
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

    [HttpPut("{attemptId:guid}/answers/{answerId:guid}/grade")]
    [ValidateCsrfToken]
    public async Task<ActionResult<GradeTestAnswerResponse>> Grade(
        Guid pollId,
        Guid attemptId,
        Guid answerId,
        GradeTestAnswerRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        TestAttemptOperationResult result = await service.GradeAsync(
            actorId, User.IsInRole(RoleNames.Admin), pollId, attemptId, answerId,
            request, cancellationToken);
        return result.Outcome == TestAttemptOperationOutcome.Succeeded
            ? Ok(result.Grade)
            : MapError<GradeTestAnswerResponse>(result);
    }

    [HttpPost("{attemptId:guid}/finalize")]
    [ValidateCsrfToken]
    public async Task<ActionResult<FinalizeTestAttemptResponse>> FinalizeAttempt(
        Guid pollId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        TestAttemptOperationResult result = await service.FinalizeAsync(
            actorId, User.IsInRole(RoleNames.Admin), pollId, attemptId,
            cancellationToken);
        return result.Outcome == TestAttemptOperationOutcome.Succeeded
            ? Ok(result.Finalization)
            : MapError<FinalizeTestAttemptResponse>(result);
    }

    [HttpGet("{attemptId:guid}/grade-history")]
    public async Task<ActionResult<TestAttemptGradeHistoryResponse>> GetGradeHistory(
        Guid pollId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        TestAttemptOperationResult result = await service.GetHistoryAsync(
            actorId, User.IsInRole(RoleNames.Admin), pollId, attemptId,
            cancellationToken);
        return result.Outcome == TestAttemptOperationOutcome.Succeeded
            ? Ok(result.History)
            : MapError<TestAttemptGradeHistoryResponse>(result);
    }

    private ActionResult<T> MapError<T>(TestAttemptOperationResult result)
    {
        if (result.Outcome == TestAttemptOperationOutcome.Invalid)
        {
            return BadRequest(new ValidationProblemDetails(
                result.ValidationErrors!.ToDictionary(item => item.Key, item => item.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = result.Error,
                Extensions = { ["code"] = result.ErrorCode }
            });
        }
        int status = result.Outcome switch
        {
            TestAttemptOperationOutcome.NotFound => StatusCodes.Status404NotFound,
            TestAttemptOperationOutcome.Forbidden => StatusCodes.Status403Forbidden,
            TestAttemptOperationOutcome.Conflict => StatusCodes.Status409Conflict,
            _ => throw new InvalidOperationException("Unknown test attempt outcome.")
        };
        return StatusCode(status, new ProblemDetails
        {
            Status = status,
            Title = result.Error,
            Extensions = { ["code"] = result.ErrorCode }
        });
    }

    private bool TryGetActorId(out Guid actorId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);
}
