using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace AnonPulse.Api.Services.Authentication;

public static class AdministrativeDeactivationCookieValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        UserManager<ApplicationUser> userManager = context.HttpContext.RequestServices
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser? user = await userManager.GetUserAsync(context.Principal);
        if (user is not null && !user.IsAdministrativelyDeactivated)
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
