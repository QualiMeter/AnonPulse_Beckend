using System.Security.Claims;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/admin/audit")]
[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminAuditController(
    IAdminAuditService auditService,
    IAdminAccessChecker accessChecker) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<PagedResponse<AdminAuditResponse>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AdminPagination.DefaultPageSize,
        [FromQuery] string? actionType = null,
        [FromQuery] string? targetType = null,
        [FromQuery] Guid? targetId = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > AdminPagination.MaximumPageSize)
        {
            ModelState.AddModelError(
                page < 1 ? "page" : "pageSize",
                page < 1
                    ? "Page must be at least 1."
                    : $"Page size must be between 1 and {AdminPagination.MaximumPageSize}.");
            return ValidationProblem(ModelState);
        }

        if (from is not null && to is not null && from > to)
        {
            ModelState.AddModelError("to", "to must not be earlier than from.");
            return ValidationProblem(ModelState);
        }

        if (!TryGetUserId(out Guid administratorId))
        {
            return Unauthorized();
        }

        if (!await accessChecker.IsActiveAdminAsync(administratorId, cancellationToken))
        {
            return Forbid();
        }

        DateTime? fromUtc = from?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime? toUtc = to?.AddDays(1).ToDateTime(
            TimeOnly.MinValue,
            DateTimeKind.Utc);
        return Ok(await auditService.GetAllAsync(
            page,
            pageSize,
            actionType,
            targetType,
            targetId,
            fromUtc,
            toUtc,
            cancellationToken));
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier),
        out userId);
}
