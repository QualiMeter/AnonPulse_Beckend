using System.Security.Claims;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/admin/dashboard")]
[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminDashboardController(
    IAdminDashboardService dashboardService,
    IAdminAccessChecker accessChecker) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<AdminDashboardResponse>> Get(
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

        return Ok(await dashboardService.GetAsync(cancellationToken));
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier),
        out userId);
}
