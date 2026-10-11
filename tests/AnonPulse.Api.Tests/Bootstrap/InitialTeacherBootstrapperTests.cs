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

public sealed class InitialTeacherBootstrapperTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_bootstrap_tests")
        .WithUsername("anonpulse_bootstrap_tests")
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
    public async Task CreateAsync_WhenUsersTableIsEmpty_CreatesTeacher()
    {
        await using ServiceProvider services = await CreateServicesAsync();
        string password = CreatePassword();
        IConfiguration configuration = CreateConfiguration(
            "initial-teacher@example.test",
            password);

        await InitialTeacherBootstrapper.CreateAsync(services, configuration);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        ApplicationUser user = Assert.Single(await userManager.Users.ToListAsync());

        Assert.Equal("initial-teacher@example.test", user.Email);
        Assert.True(user.EmailConfirmed);
        Assert.True(await roleManager.RoleExistsAsync(RoleNames.Teacher));
        Assert.True(await userManager.IsInRoleAsync(user, RoleNames.Teacher));
        Assert.True(await userManager.CheckPasswordAsync(user, password));
    }

    [Fact]
    public async Task CreateAsync_WhenRunTwice_RejectsSecondRunWithoutChanges()
    {
        await using ServiceProvider services = await CreateServicesAsync();
        IConfiguration firstConfiguration = CreateConfiguration(
            "initial-teacher@example.test",
            CreatePassword());
        await InitialTeacherBootstrapper.CreateAsync(services, firstConfiguration);

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext =
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.Roles.RemoveRange(dbContext.Roles);
            await dbContext.SaveChangesAsync();
        }

        IConfiguration secondConfiguration = CreateConfiguration(
            "another-teacher@example.test",
            CreatePassword());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InitialTeacherBootstrapper.CreateAsync(services, secondConfiguration));

        Assert.Contains("users table is empty", exception.Message, StringComparison.Ordinal);

        await using AsyncServiceScope verificationScope = services.CreateAsyncScope();
        ApplicationDbContext verificationDbContext =
            verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser user = Assert.Single(await verificationDbContext.Users.ToListAsync());
        Assert.Equal("initial-teacher@example.test", user.Email);
        Assert.Empty(await verificationDbContext.Roles.ToListAsync());
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
                ["Bootstrap:Email"] = email,
                ["Bootstrap:Password"] = password
            })
            .Build();
    }

    private static string CreatePassword()
    {
        return $"Bootstrap-{Guid.NewGuid():N}-aA1!";
    }
}
