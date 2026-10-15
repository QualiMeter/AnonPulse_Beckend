using AnonPulse.Api.Contracts.Auth;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IPasswordHasher<ApplicationUser> passwordHasher,
    TimeProvider timeProvider,
    IOptions<AuthenticationSessionOptions> sessionOptions) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<CurrentUserResponse>> Login(
        LoginRequest request)
    {
        ApplicationUser? user = await userManager.FindByEmailAsync(request.Email);

        if (user is null)
        {
            _ = passwordHasher.HashPassword(new ApplicationUser(), request.Password);
            return InvalidCredentials();
        }

        Microsoft.AspNetCore.Identity.SignInResult result =
            await signInManager.CheckPasswordSignInAsync(
                user,
                request.Password,
                lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            return InvalidCredentials();
        }

        TimeSpan lifetime = request.RememberMe
            ? sessionOptions.Value.PersistentLifetime
            : sessionOptions.Value.SessionLifetime;
        await signInManager.SignInAsync(user, new AuthenticationProperties
        {
            IsPersistent = request.RememberMe,
            AllowRefresh = true,
            ExpiresUtc = timeProvider.GetUtcNow().Add(lifetime)
        });

        return Ok(await CreateCurrentUserResponseAsync(user));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();

        return Ok(new { message = "Signed out." });
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> Me()
    {
        ApplicationUser? user = await userManager.GetUserAsync(User);

        if (user is null)
        {
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Authentication is required."
            });
        }

        return Ok(await CreateCurrentUserResponseAsync(user));
    }

    private UnauthorizedObjectResult InvalidCredentials()
    {
        return Unauthorized(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Invalid email or password."
        });
    }

    private async Task<CurrentUserResponse> CreateCurrentUserResponseAsync(
        ApplicationUser user)
    {
        IList<string> roles = await userManager.GetRolesAsync(user);

        return new CurrentUserResponse(
            user.Id,
            user.Email!,
            roles.Order(StringComparer.Ordinal).ToArray());
    }
}
