using System.Data;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AnonPulse.Api.Bootstrap;

public static class InitialTeacherBootstrapper
{
    private const string EmailConfigurationKey = "Bootstrap:Email";
    private const string PasswordConfigurationKey = "Bootstrap:Password";

    public static async Task CreateAsync(
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        string email = GetRequiredEmail(configuration);
        string password = GetRequiredPassword(configuration);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ApplicationDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        if (await userManager.Users.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Initial teacher bootstrap is allowed only when the users table is empty.");
        }

        if (!await roleManager.RoleExistsAsync(RoleNames.Teacher))
        {
            IdentityResult roleResult = await roleManager.CreateAsync(
                new IdentityRole<Guid>(RoleNames.Teacher));
            EnsureSucceeded(roleResult, "create the teacher role");
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };

        IdentityResult userResult = await userManager.CreateAsync(user, password);
        EnsureSucceeded(userResult, "create the initial teacher");

        IdentityResult roleAssignmentResult = await userManager.AddToRoleAsync(
            user,
            RoleNames.Teacher);
        EnsureSucceeded(roleAssignmentResult, "assign the teacher role");

        await transaction.CommitAsync(cancellationToken);

        Console.WriteLine("Initial teacher created successfully.");
    }

    private static string GetRequiredEmail(IConfiguration configuration)
    {
        string? email = configuration[EmailConfigurationKey];

        if (string.IsNullOrWhiteSpace(email))
        {
            throw MissingConfiguration(EmailConfigurationKey);
        }

        return email.Trim();
    }

    private static string GetRequiredPassword(IConfiguration configuration)
    {
        string? password = configuration[PasswordConfigurationKey];

        if (string.IsNullOrEmpty(password))
        {
            throw MissingConfiguration(PasswordConfigurationKey);
        }

        return password;
    }

    private static InvalidOperationException MissingConfiguration(string key)
    {
        return new InvalidOperationException(
            $"Configuration value '{key}' is required.");
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
