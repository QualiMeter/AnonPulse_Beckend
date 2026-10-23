using System.Security.Claims;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Models;
using AnonPulse.Api.Security;
using AnonPulse.Api.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminUsersController(
    IAdminUserService userService,
    IAdminAccessChecker accessChecker) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<PagedResponse<AdminUserListItemResponse>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AdminPagination.DefaultPageSize,
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        [FromQuery] string? state = null,
        CancellationToken cancellationToken = default)
    {
        ActionResult? validation = ValidatePage(page, pageSize);
        if (validation is not null)
        {
            return validation;
        }

        if (!TryParseState(state, out bool? isDeactivated))
        {
            return ValidationError("state", "State must be 'active' or 'blocked'.");
        }

        if (!string.IsNullOrWhiteSpace(role) &&
            !string.Equals(role, RoleNames.Teacher, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(role, RoleNames.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return ValidationError("role", "Role must be 'teacher' or 'admin'.");
        }

        if (!await IsActiveAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        return Ok(await userService.GetAllAsync(
            page,
            pageSize,
            search,
            role,
            isDeactivated,
            cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<AdminUserDetailsResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!await IsActiveAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        AdminUserDetailsResponse? user = await userService.GetByIdAsync(
            id,
            cancellationToken);
        return user is null
            ? NotFoundProblem("User was not found.")
            : Ok(user);
    }

    [HttpPatch("{id:guid}/status")]
    [ValidateCsrfToken]
    public async Task<ActionResult<AdminUserDetailsResponse>> SetStatus(
        Guid id,
        UpdateAdminUserStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid administratorId))
        {
            return Unauthorized();
        }

        if (!await accessChecker.IsActiveAdminAsync(administratorId, cancellationToken))
        {
            return Forbid();
        }

        AdminUserMutationResult result = await userService.SetDeactivatedAsync(
            administratorId,
            id,
            request.IsDeactivated,
            cancellationToken);
        return ToMutationResult(result);
    }

    [HttpDelete("{id:guid}")]
    [ValidateCsrfToken]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid administratorId))
        {
            return Unauthorized();
        }

        if (!await accessChecker.IsActiveAdminAsync(administratorId, cancellationToken))
        {
            return Forbid();
        }

        AdminUserMutationResult result = await userService.DeleteAsync(
            administratorId,
            id,
            cancellationToken);
        if (result.Outcome == AdminUserMutationOutcome.Succeeded)
        {
            return NoContent();
        }

        return ToMutationResult(result).Result!;
    }

    private ActionResult<AdminUserDetailsResponse> ToMutationResult(
        AdminUserMutationResult result)
    {
        return result.Outcome switch
        {
            AdminUserMutationOutcome.Succeeded => Ok(result.User),
            AdminUserMutationOutcome.NotFound => NotFoundProblem("User was not found."),
            AdminUserMutationOutcome.Conflict => Conflict(CreateConflictProblem(result)),
            AdminUserMutationOutcome.Failed => StatusCode(
                StatusCodes.Status500InternalServerError,
                new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "User operation failed."
                }),
            _ => throw new InvalidOperationException(
                $"Unknown user mutation outcome: {result.Outcome}.")
        };
    }

    private static ProblemDetails CreateConflictProblem(AdminUserMutationResult result)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "User operation is not allowed.",
            Detail = result.Error
        };
        problem.Extensions["code"] = result.Code;
        if (result.Code == "user_has_dependencies")
        {
            problem.Extensions["pollCount"] = result.PollDependencies;
            problem.Extensions["decisionCount"] = result.DecisionDependencies;
            problem.Extensions["alternative"] = "Deactivate the user instead.";
        }

        return problem;
    }

    private async Task<bool> IsActiveAdminAsync(CancellationToken cancellationToken) =>
        TryGetUserId(out Guid userId) &&
        await accessChecker.IsActiveAdminAsync(userId, cancellationToken);

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier),
        out userId);

    private ActionResult? ValidatePage(int page, int pageSize)
    {
        if (page < 1)
        {
            return ValidationError("page", "Page must be at least 1.");
        }

        return pageSize is < 1 or > AdminPagination.MaximumPageSize
            ? ValidationError(
                "pageSize",
                $"Page size must be between 1 and {AdminPagination.MaximumPageSize}.")
            : null;
    }

    private static bool TryParseState(string? state, out bool? isDeactivated)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            isDeactivated = null;
            return true;
        }

        if (string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
        {
            isDeactivated = false;
            return true;
        }

        if (string.Equals(state, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            isDeactivated = true;
            return true;
        }

        isDeactivated = null;
        return false;
    }

    private ActionResult ValidationError(string key, string message)
    {
        ModelState.AddModelError(key, message);
        return ValidationProblem(ModelState);
    }

    private NotFoundObjectResult NotFoundProblem(string title) => NotFound(
        new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = title
        });
}
