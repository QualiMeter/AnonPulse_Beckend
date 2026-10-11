using System.Security.Claims;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Polls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Authorize(Roles = RoleNames.Teacher)]
[Route("api/polls")]
public sealed class PollsController(IPollService pollService) : ControllerBase
{
    [HttpPost]
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

    private bool TryGetCurrentUserId(out Guid userId)
    {
        return Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out userId);
    }
}
