using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AnonPulse.Api.Tests.Auth;

public sealed class AuthWebApplicationFactory : WebApplicationFactory<Program>
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
        });
    }

    public HttpClient CreateHttpsClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    public async Task<SeededUser> CreateTeacherAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var role = new IdentityRole<Guid>(RoleNames.Teacher);
        IdentityResult roleResult = await roleManager.CreateAsync(role);
        Assert.True(roleResult.Succeeded, FormatErrors(roleResult));

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "teacher@example.test",
            UserName = "teacher@example.test",
            EmailConfirmed = true
        };
        const string password = "Teacher-password-123!";
        IdentityResult userResult = await userManager.CreateAsync(user, password);
        Assert.True(userResult.Succeeded, FormatErrors(userResult));

        IdentityResult assignmentResult = await userManager.AddToRoleAsync(
            user,
            RoleNames.Teacher);
        Assert.True(assignmentResult.Succeeded, FormatErrors(assignmentResult));

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
