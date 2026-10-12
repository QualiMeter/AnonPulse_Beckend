using AnonPulse.Api.Bootstrap;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Bootstrap;

public sealed class AdminBootstrapperTests : IAsyncLifetime
{
    private const string AdminEmail = "suhoguzov@gmail.com";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_admin_bootstrap_tests")
        .WithUsername("anonpulse_admin_bootstrap_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync()
    {
        return _postgres.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _postgres.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task CreateAsync_CreatesAdministratorWithOnlyAdminRole()
    {
        await using ServiceProvider services = await CreateServicesAsync();
        string password = CreatePassword();

        await using (AsyncServiceScope existingUserScope = services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> existingUserManager = existingUserScope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            var existingUser = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                Email = "existing-teacher@example.test",
                UserName = "existing-teacher@example.test"
            };
            IdentityResult existingUserResult = await existingUserManager.CreateAsync(
                existingUser,
                CreatePassword());
            Assert.True(existingUserResult.Succeeded);
        }

        await AdminBootstrapper.CreateAsync(
            services,
            CreateConfiguration(AdminEmail, password));

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        ApplicationUser? user = await userManager.FindByEmailAsync(AdminEmail);

        Assert.NotNull(user);
        Assert.Equal(2, await userManager.Users.CountAsync());
        Assert.Equal(AdminEmail, user.Email);
        Assert.True(user.EmailConfirmed);
        Assert.True(await roleManager.RoleExistsAsync(RoleNames.Admin));
        Assert.True(await userManager.IsInRoleAsync(user, RoleNames.Admin));
        Assert.False(await userManager.IsInRoleAsync(user, RoleNames.Teacher));
        Assert.True(await userManager.CheckPasswordAsync(user, password));
    }

    [Fact]
    public async Task CreateAsync_WhenRunTwice_RejectsSecondRunWithoutChanges()
    {
        await using ServiceProvider services = await CreateServicesAsync();
        string originalPassword = CreatePassword();
        await AdminBootstrapper.CreateAsync(
            services,
            CreateConfiguration(AdminEmail, originalPassword));

        string? originalPasswordHash;
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext =
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            originalPasswordHash = (await dbContext.Users.SingleAsync()).PasswordHash;
        }

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AdminBootstrapper.CreateAsync(
                services,
                CreateConfiguration(AdminEmail, CreatePassword())));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);

        await using AsyncServiceScope verificationScope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = verificationScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = Assert.Single(await userManager.Users.ToListAsync());
        IList<string> roles = await userManager.GetRolesAsync(user);

        Assert.Equal(originalPasswordHash, user.PasswordHash);
        Assert.True(await userManager.CheckPasswordAsync(user, originalPassword));
        Assert.Equal([RoleNames.Admin], roles);
    }

    [Fact]
    public async Task CreateAsync_WhenEmailBelongsToExistingUser_DoesNotGrantAdminOrChangePassword()
    {
        await using ServiceProvider services = await CreateServicesAsync();
        string originalPassword = CreatePassword();

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> userManager =
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            RoleManager<IdentityRole<Guid>> roleManager =
                scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            IdentityResult roleResult = await roleManager.CreateAsync(
                new IdentityRole<Guid>(RoleNames.Teacher));
            Assert.True(roleResult.Succeeded);

            var existingUser = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                Email = AdminEmail,
                UserName = AdminEmail,
                EmailConfirmed = true
            };
            IdentityResult userResult = await userManager.CreateAsync(
                existingUser,
                originalPassword);
            Assert.True(userResult.Succeeded);
            IdentityResult assignmentResult = await userManager.AddToRoleAsync(
                existingUser,
                RoleNames.Teacher);
            Assert.True(assignmentResult.Succeeded);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AdminBootstrapper.CreateAsync(
                services,
                CreateConfiguration(AdminEmail, CreatePassword())));

        await using AsyncServiceScope verificationScope = services.CreateAsyncScope();
        UserManager<ApplicationUser> verificationUserManager = verificationScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        RoleManager<IdentityRole<Guid>> verificationRoleManager = verificationScope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        ApplicationUser user = Assert.Single(await verificationUserManager.Users.ToListAsync());

        Assert.True(await verificationUserManager.CheckPasswordAsync(user, originalPassword));
        Assert.True(await verificationUserManager.IsInRoleAsync(user, RoleNames.Teacher));
        Assert.False(await verificationUserManager.IsInRoleAsync(user, RoleNames.Admin));
        Assert.False(await verificationRoleManager.RoleExistsAsync(RoleNames.Admin));
    }

    [Fact]
    public async Task CreateAsync_WithDifferentConfiguredEmail_RejectsWithoutDatabaseChanges()
    {
        await using ServiceProvider services = await CreateServicesAsync();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AdminBootstrapper.CreateAsync(
                services,
                CreateConfiguration("other-admin@example.test", CreatePassword())));

        Assert.Contains(AdminEmail, exception.Message, StringComparison.Ordinal);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ApplicationDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await dbContext.Users.ToListAsync());
        Assert.Empty(await dbContext.Roles.ToListAsync());
    }

    private async Task<ServiceProvider> CreateServicesAsync()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()));
        serviceCollection
            .AddIdentityCore<ApplicationUser>(options =>
                options.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        ServiceProvider services = serviceCollection.BuildServiceProvider();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ApplicationDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();

        return services;
    }

    private static IConfiguration CreateConfiguration(string email, string password)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bootstrap:AdminEmail"] = email,
                ["Bootstrap:AdminPassword"] = password
            })
            .Build();
    }

    private static string CreatePassword()
    {
        return $"Admin-{Guid.NewGuid():N}-aA1!";
    }
}
