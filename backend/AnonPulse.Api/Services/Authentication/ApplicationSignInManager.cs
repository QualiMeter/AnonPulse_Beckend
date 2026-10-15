using System.Security.Claims;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.Authentication;

public sealed class ApplicationSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> identityOptions,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    TimeProvider timeProvider,
    IOptions<AuthenticationSessionOptions> sessionOptions)
    : SignInManager<ApplicationUser>(
        userManager,
        contextAccessor,
        claimsFactory,
        identityOptions,
        logger,
        schemes,
        confirmation)
{
    public override Task SignInWithClaimsAsync(
        ApplicationUser user,
        bool isPersistent,
        IEnumerable<Claim> additionalClaims)
    {
        TimeSpan lifetime = isPersistent
            ? sessionOptions.Value.PersistentLifetime
            : sessionOptions.Value.SessionLifetime;

        return base.SignInWithClaimsAsync(
            user,
            new AuthenticationProperties
            {
                IsPersistent = isPersistent,
                AllowRefresh = true,
                ExpiresUtc = timeProvider.GetUtcNow().Add(lifetime)
            },
            additionalClaims);
    }
}
