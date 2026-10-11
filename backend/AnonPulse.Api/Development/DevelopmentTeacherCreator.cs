using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Identity;

namespace AnonPulse.Api.Development;

public static class DevelopmentTeacherCreator
{
    private const string EmailConfigurationKey = "DevelopmentTeacher:Email";
    private const string PasswordConfigurationKey = "DevelopmentTeacher:Password";

    public static async Task CreateAsync(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "The development teacher command can run only in Development.");
        }

        string email = app.Configuration[EmailConfigurationKey]
            ?? throw MissingConfiguration(EmailConfigurationKey);
        string password = app.Configuration[PasswordConfigurationKey]
            ?? throw MissingConfiguration(PasswordConfigurationKey);

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        if (!await roleManager.RoleExistsAsync(RoleNames.Teacher))
        {
            IdentityResult roleResult = await roleManager.CreateAsync(
                new IdentityRole<Guid>(RoleNames.Teacher));
            EnsureSucceeded(roleResult, "create the teacher role");
        }

        ApplicationUser? user = await userManager.FindByEmailAsync(email);

        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                Email = email,
                UserName = email,
                EmailConfirmed = true
            };

            IdentityResult userResult = await userManager.CreateAsync(user, password);
            EnsureSucceeded(userResult, "create the development teacher");
        }

        if (!await userManager.IsInRoleAsync(user, RoleNames.Teacher))
        {
            IdentityResult roleAssignmentResult = await userManager.AddToRoleAsync(
                user,
                RoleNames.Teacher);
            EnsureSucceeded(roleAssignmentResult, "assign the teacher role");
        }

        Console.WriteLine("Development teacher is ready.");
    }

    private static InvalidOperationException MissingConfiguration(string key)
    {
        return new InvalidOperationException(
            $"Configuration value '{key}' is required. " +
            "Set it with User Secrets or an environment variable.");
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        string errors = string.Join(
            "; ",
            result.Errors.Select(error => $"{error.Code}: {error.Description}"));

        throw new InvalidOperationException($"Failed to {operation}: {errors}");
    }
}
