using System.Security.Claims;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Models;
using AnonPulse.Api.Security;
using AnonPulse.Api.Services.Admin;
using AnonPulse.Api.Services.Polls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/polls")]
public sealed class PollsController(
    IPollService pollService,
    IPollDeletionService deletionService) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = RoleNames.Teacher)]
    public async Task<ActionResult<PollResponse>> Create(
        CreatePollRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid creatorId))
        {
            return Unauthorized();
        }

        PollCreationResult result = await pollService.CreateAsync(
            creatorId,
            request,
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach ((string key, string[] messages) in result.ValidationErrors)
            {
                foreach (string message in messages)
                {
                    ModelState.AddModelError(key, message);
                }
            }

            return ValidationProblem(ModelState);
        }

        PollResponse poll = result.Poll!;

        return CreatedAtAction(
            nameof(GetById),
            new { id = poll.Id },
            poll);
    }

    [HttpGet]
    [Authorize(Roles = RoleNames.Teacher)]
    public async Task<ActionResult<IReadOnlyList<PollResponse>>> GetAll(
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid creatorId))
        {
            return Unauthorized();
        }

        IReadOnlyList<PollResponse> polls = await pollService.GetAllAsync(
            creatorId,
            cancellationToken);

        return Ok(polls);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = RoleNames.Teacher)]
    public async Task<ActionResult<PollResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid creatorId))
        {
            return Unauthorized();
        }

        PollResponse? poll = await pollService.GetByIdAsync(
            creatorId,
            id,
            cancellationToken);

        return poll is null ? NotFound() : Ok(poll);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = RoleNames.Teacher)]
    public async Task<ActionResult<PollResponse>> Update(
        Guid id,
        CreatePollRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid creatorId))
        {
            return Unauthorized();
        }

        PollUpdateResult result = await pollService.UpdateAsync(
            creatorId,
            id,
            request,
            cancellationToken);

        return result.Outcome switch
        {
            PollUpdateOutcome.Succeeded => Ok(result.Poll),
            PollUpdateOutcome.Invalid => BadRequest(new ValidationProblemDetails(
                result.ValidationErrors!.ToDictionary(item => item.Key, item => item.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred."
            }),
            PollUpdateOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Poll was not found."
            }),
            PollUpdateOutcome.Conflict => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Poll cannot be edited.",
                Detail = result.Error
            }),
            _ => throw new InvalidOperationException(
                $"Unknown poll update outcome: {result.Outcome}.")
        };
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = RoleNames.Teacher)]
    [ValidateCsrfToken]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid teacherId))
        {
            return Unauthorized();
        }

        PollDeletionResult result = await deletionService.DeleteTeacherDraftAsync(
            teacherId,
            id,
            cancellationToken);
        return result.Outcome switch
        {
            PollDeletionOutcome.Succeeded => NoContent(),
            PollDeletionOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Poll was not found."
            }),
            PollDeletionOutcome.Conflict => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Poll cannot be deleted.",
                Detail = result.Error,
                Extensions = { ["code"] = result.Code }
            }),
            _ => throw new InvalidOperationException(
                $"Unknown poll deletion outcome: {result.Outcome}.")
        };
    }

    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = RoleNames.Teacher + "," + RoleNames.Admin)]
    public async Task<ActionResult<PollResponse>> Publish(
        Guid id,
        CancellationToken cancellationToken)
    {
        return await ChangeStatusAsync(id, publish: true, cancellationToken);
    }

    [HttpPost("{id:guid}/close")]
    [Authorize(Roles = RoleNames.Teacher + "," + RoleNames.Admin)]
    public async Task<ActionResult<PollResponse>> Close(
        Guid id,
        CancellationToken cancellationToken)
    {
        return await ChangeStatusAsync(id, publish: false, cancellationToken);
    }

    private async Task<ActionResult<PollResponse>> ChangeStatusAsync(
        Guid pollId,
        bool publish,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid actorId))
        {
            return Unauthorized();
        }

        bool isAdmin = User.IsInRole(RoleNames.Admin);
        PollTransitionResult result = publish
            ? await pollService.PublishAsync(actorId, isAdmin, pollId, cancellationToken)
            : await pollService.CloseAsync(actorId, isAdmin, pollId, cancellationToken);

        return result.Outcome switch
        {
            PollTransitionOutcome.Succeeded => Ok(result.Poll),
            PollTransitionOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Poll was not found."
            }),
            PollTransitionOutcome.Conflict => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Poll state transition is not allowed.",
                Detail = result.Error
            }),
            _ => throw new InvalidOperationException(
                $"Unknown poll transition outcome: {result.Outcome}.")
        };
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        return Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out userId);
    }
}
