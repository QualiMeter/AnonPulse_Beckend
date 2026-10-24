using System.Security.Claims;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Models;
using AnonPulse.Api.Security;
using AnonPulse.Api.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/admin/polls")]
[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminPollsController(
    IAdminPollService pollService,
    IPollDeletionService deletionService,
    IAdminAccessChecker accessChecker) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<PagedResponse<AdminPollListItemResponse>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AdminPagination.DefaultPageSize,
        [FromQuery] string? search = null,
        [FromQuery] Guid? creatorId = null,
        [FromQuery] string? status = null,
        [FromQuery] DateOnly? createdFrom = null,
        [FromQuery] DateOnly? createdTo = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateQuery(
                page,
                pageSize,
                status,
                createdFrom,
                createdTo,
                out PollStatus? parsedStatus,
                out ActionResult? error))
        {
            return error!;
        }

        if (!await IsActiveAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        DateTime? fromUtc = createdFrom?.ToDateTime(
            TimeOnly.MinValue,
            DateTimeKind.Utc);
        DateTime? toUtc = createdTo?.AddDays(1).ToDateTime(
            TimeOnly.MinValue,
            DateTimeKind.Utc);
        return Ok(await pollService.GetAllAsync(
            page,
            pageSize,
            search,
            creatorId,
            parsedStatus,
            fromUtc,
            toUtc,
            cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<AdminPollDetailsResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!await IsActiveAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        AdminPollDetailsResponse? poll = await pollService.GetByIdAsync(
            id,
            cancellationToken);
        return poll is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Poll was not found."
            })
            : Ok(poll);
    }

    [HttpDelete("{id:guid}")]
    [ValidateCsrfToken]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out Guid administratorId))
        {
            return Unauthorized();
        }

        if (!await accessChecker.IsActiveAdminAsync(administratorId, cancellationToken))
        {
            return Forbid();
        }

        PollDeletionResult result = await deletionService.DeleteAdminAsync(
            administratorId,
            id,
            confirm,
            cancellationToken);
        return ToDeletionResult(result);
    }

    private IActionResult ToDeletionResult(PollDeletionResult result) =>
        result.Outcome switch
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

    private bool ValidateQuery(
        int page,
        int pageSize,
        string? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        out PollStatus? parsedStatus,
        out ActionResult? error)
    {
        parsedStatus = null;
        error = null;
        if (page < 1)
        {
            error = ValidationError("page", "Page must be at least 1.");
            return false;
        }

        if (pageSize is < 1 or > AdminPagination.MaximumPageSize)
        {
            error = ValidationError(
                "pageSize",
                $"Page size must be between 1 and {AdminPagination.MaximumPageSize}.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse(status, true, out PollStatus value))
            {
                error = ValidationError(
                    "status",
                    "Status must be 'draft', 'open', or 'closed'.");
                return false;
            }

            parsedStatus = value;
        }

        if (createdFrom is not null && createdTo is not null &&
            createdFrom > createdTo)
        {
            error = ValidationError(
                "createdTo",
                "createdTo must not be earlier than createdFrom.");
            return false;
        }

        return true;
    }

    private async Task<bool> IsActiveAdminAsync(CancellationToken cancellationToken) =>
        TryGetUserId(out Guid userId) &&
        await accessChecker.IsActiveAdminAsync(userId, cancellationToken);

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier),
        out userId);

    private ActionResult ValidationError(string key, string message)
    {
        ModelState.AddModelError(key, message);
        return ValidationProblem(ModelState);
    }
}
