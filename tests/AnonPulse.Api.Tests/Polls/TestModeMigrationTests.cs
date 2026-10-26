using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class TestModeMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261025072432_AddFeedbackAnalyses";
    private const string CurrentMigration = "20261026043614_AddTestModeAndNamedAttempts";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_test_mode_migration")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_PreservesAnonymousAndIdentifiedSurveys_AndMatchesSqlScript()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        Guid owner = Guid.NewGuid();
        Guid anonymousPoll = Guid.NewGuid();
        Guid identifiedPoll = Guid.NewGuid();
        Guid submission = Guid.NewGuid();
        await using (var context = new ApplicationDbContext(options))
        {
            IMigrator migrator = context.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users
                    ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount", is_administratively_deactivated)
                VALUES ({owner}, FALSE, FALSE, FALSE, FALSE, 0, FALSE);
                INSERT INTO polls (id, creator_id, status, response_mode, created_at)
                VALUES
                    ({anonymousPoll}, {owner}, 'closed', 'anonymous', {DateTime.UtcNow}),
                    ({identifiedPoll}, {owner}, 'closed', 'identified', {DateTime.UtcNow});
                INSERT INTO identified_poll_submissions (id, poll_id, respondent_name)
                VALUES ({submission}, {identifiedPoll}, 'Legacy respondent');
                """);
            string generated = migrator.GenerateScript(
                PreviousMigration, CurrentMigration,
                MigrationsSqlGenerationOptions.Idempotent);
            string manual = await File.ReadAllTextAsync(FindScript());
            Assert.Equal(Normalize(generated), Normalize(manual));
            await context.Database.ExecuteSqlRawAsync(manual);
        }

        await using var verify = new ApplicationDbContext(options);
        Poll[] polls = await verify.Polls.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, polls.Length);
        Assert.All(polls, poll =>
        {
            Assert.Equal(PollKind.Survey, poll.Kind);
            Assert.Null(poll.TestScale);
        });
        IdentifiedPollSubmission legacy =
            await verify.IdentifiedPollSubmissions.AsNoTracking().SingleAsync();
        Assert.Equal(identifiedPoll, legacy.PollId);
        Assert.Empty(verify.NamedAttempts);
        Assert.Empty(verify.PollQuestionCorrectOptions);
    }

    private static string FindScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "scripts", "migrations",
                "20261026043614_add_test_mode_and_named_attempts.sql");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Test mode migration script was not found.");
    }

    private static string Normalize(string sql) => sql.TrimStart('\uFEFF')
        .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
