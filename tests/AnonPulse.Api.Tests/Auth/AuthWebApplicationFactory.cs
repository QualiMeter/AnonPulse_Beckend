using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AnonPulse.Api.Tests.Auth;

public sealed class AuthWebApplicationFactory(TimeProvider? timeProvider = null)
    : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"auth-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ApplicationDbContext>();
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            if (timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
                services.PostConfigure<CookieAuthenticationOptions>(
                    IdentityConstants.ApplicationScheme,
                    options => options.TimeProvider = timeProvider);
            }
        });
    }

    public HttpClient CreateHttpsClient(bool handleCookies = true)
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = handleCookies,
            AllowAutoRedirect = false
        });
    }

    public async Task<SeededUser> CreateTeacherAsync()
    {
        return await CreateUserAsync(
            $"teacher-{Guid.NewGuid()}@example.test",
            RoleNames.Teacher);
    }

    public async Task<SeededUser> CreateTwoFactorTeacherAsync()
    {
        SeededUser seededUser = await CreateTeacherAsync();
        using IServiceScope scope = Services.CreateScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await userManager.FindByIdAsync(seededUser.Id.ToString())
            ?? throw new InvalidOperationException("Seeded user was not found.");

        IdentityResult authenticatorResult = await userManager.ResetAuthenticatorKeyAsync(user);
        Assert.True(authenticatorResult.Succeeded, FormatErrors(authenticatorResult));
        IdentityResult twoFactorResult = await userManager.SetTwoFactorEnabledAsync(user, true);
        Assert.True(twoFactorResult.Succeeded, FormatErrors(twoFactorResult));

        return seededUser;
    }

    public async Task<SeededUser> CreateUserAsync(
        string email,
        string? role = null)
    {
        using IServiceScope scope = Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        if (role is not null && !await roleManager.RoleExistsAsync(role))
        {
            IdentityResult roleResult = await roleManager.CreateAsync(
                new IdentityRole<Guid>(role));
            Assert.True(roleResult.Succeeded, FormatErrors(roleResult));
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        const string password = "Teacher-password-123!";
        IdentityResult userResult = await userManager.CreateAsync(user, password);
        Assert.True(userResult.Succeeded, FormatErrors(userResult));

        if (role is not null)
        {
            IdentityResult assignmentResult = await userManager.AddToRoleAsync(user, role);
            Assert.True(assignmentResult.Succeeded, FormatErrors(assignmentResult));
        }

        return new SeededUser(user.Id, user.Email, password);
    }

    private static string FormatErrors(IdentityResult result)
    {
        return string.Join(
            "; ",
            result.Errors.Select(error => $"{error.Code}: {error.Description}"));
    }
}

public sealed record SeededUser(Guid Id, string Email, string Password);
