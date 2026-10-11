using AnonPulse.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.DataProtection;

public sealed class DataProtectionPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_data_protection_tests")
        .WithUsername("anonpulse_data_protection_tests")
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
    public async Task ProtectedPayload_CanBeReadByNewApplicationInstance()
    {
        await using (ServiceProvider migrationServices = CreateApplicationServices())
        {
            await using AsyncServiceScope scope = migrationServices.CreateAsyncScope();
            ApplicationDbContext dbContext =
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        string protectedPayload;

        await using (ServiceProvider firstApplication = CreateApplicationServices())
        {
            IDataProtector protector = firstApplication
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("data-protection-persistence-test");
            protectedPayload = protector.Protect("payload from first application");
        }

        await using (ServiceProvider secondApplication = CreateApplicationServices())
        {
            IDataProtector protector = secondApplication
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("data-protection-persistence-test");
            string payload = protector.Unprotect(protectedPayload);

            Assert.Equal("payload from first application", payload);

            await using AsyncServiceScope scope = secondApplication.CreateAsyncScope();
            ApplicationDbContext dbContext =
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.NotEmpty(await dbContext.DataProtectionKeys.ToListAsync());
        }
    }

    private ServiceProvider CreateApplicationServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()));
        services
            .AddDataProtection()
            .SetApplicationName("AnonPulse")
            .PersistKeysToDbContext<ApplicationDbContext>();

        return services.BuildServiceProvider();
    }
}
