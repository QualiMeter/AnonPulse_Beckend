using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.FeedbackAnalysis;

public sealed class AiTokenBudgetManagerPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_ai_budget_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N")).Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task SettlementUsesLockedDatabaseState_AndCrashRecoveryIsConservative()
    {
        DbContextOptions<ApplicationDbContext> dbOptions =
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(_postgres.GetConnectionString()).Options;
        await using (var migrate = new ApplicationDbContext(dbOptions))
            await migrate.Database.MigrateAsync();
        var options = Options.Create(new AiOptions
        {
            Enabled = true,
            TimeoutSeconds = 5,
            SharedTokenBudget = 1_500,
            SharedTokenBudgetPeriodMinutes = 60
        });
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 26, 10, 0, 0,
            TimeSpan.Zero));

        await using var workerADb = new ApplicationDbContext(dbOptions);
        await using var workerBDb = new ApplicationDbContext(dbOptions);
        var workerA = new AiTokenBudgetManager(workerADb, options, time);
        var workerB = new AiTokenBudgetManager(workerBDb, options, time);
        AiBudgetReservationResult reservationA = await workerA.ReserveAsync(
            "feedback_analysis", Guid.NewGuid(), 1, 700, default);
        AiBudgetReservationResult reservationB = await workerB.ReserveAsync(
            "knowledge_grading", Guid.NewGuid(), 1, 700, default);
        Assert.True(reservationA.Succeeded);
        Assert.True(reservationB.Succeeded);

        await using (var thirdDb = new ApplicationDbContext(dbOptions))
        {
            AiBudgetReservationResult third = await new AiTokenBudgetManager(
                thirdDb, options, time).ReserveAsync("feedback_analysis", Guid.NewGuid(), 1,
                200, default);
            Assert.False(third.Succeeded);
        }

        await workerA.SettleAsync(reservationA.ReservationId!.Value, 200, 100, false, default);
        await AssertPeriodAsync(dbOptions, reserved: 700, estimated: 300, actual: 300);
        await workerB.SettleAsync(reservationB.ReservationId!.Value, 300, 200, false, default);
        await AssertPeriodAsync(dbOptions, reserved: 0, estimated: 800, actual: 800);

        time.Advance(TimeSpan.FromHours(2));
        await using (var releaserADb = new ApplicationDbContext(dbOptions))
        await using (var releaserBDb = new ApplicationDbContext(dbOptions))
        {
            var releaserA = new AiTokenBudgetManager(releaserADb, options, time);
            var releaserB = new AiTokenBudgetManager(releaserBDb, options, time);
            AiBudgetReservationResult releaseA = await releaserA.ReserveAsync(
                "feedback_analysis", Guid.NewGuid(), 1, 600, default);
            AiBudgetReservationResult releaseB = await releaserB.ReserveAsync(
                "knowledge_grading", Guid.NewGuid(), 1, 600, default);
            await releaserA.ReleaseAsync(releaseA.ReservationId!.Value, default);
            await AssertLatestPeriodAsync(dbOptions, reserved: 600, estimated: 0, actual: 0);
            await releaserB.ReleaseAsync(releaseB.ReservationId!.Value, default);
            await AssertLatestPeriodAsync(dbOptions, reserved: 0, estimated: 0, actual: 0);
        }

        time.Advance(TimeSpan.FromHours(2));
        Guid crashedWorkload = Guid.NewGuid();
        Guid crashedReservation;
        await using (var crashedDb = new ApplicationDbContext(dbOptions))
        {
            AiBudgetReservationResult reserved = await new AiTokenBudgetManager(
                crashedDb, options, time).ReserveAsync("knowledge_grading", crashedWorkload, 1,
                700, default);
            crashedReservation = reserved.ReservationId!.Value;
        }
        time.Advance(TimeSpan.FromSeconds(36));
        await using (var restartedDb = new ApplicationDbContext(dbOptions))
        {
            var restarted = new AiTokenBudgetManager(restartedDb, options, time);
            await restarted.ReconcileStaleAsync(default);
            await restarted.ReconcileStaleAsync(default);
        }

        await using (var verify = new ApplicationDbContext(dbOptions))
        {
            AiTokenBudgetPeriod period = await verify.AiTokenBudgetPeriods
                .OrderByDescending(item => item.PeriodStart).FirstAsync();
            Assert.Equal(0, period.ReservedTokens);
            Assert.Equal(700, period.EstimatedConsumedTokens);
            Assert.Equal(0, period.ActualTokens);
            AiTokenBudgetReservation reservation = await verify.AiTokenBudgetReservations
                .SingleAsync(item => item.Id == crashedReservation);
            Assert.Equal("settled", reservation.Status);
            Assert.Equal(700, reservation.ChargedTokens);
            Assert.Null(reservation.ActualPromptTokens);
            Assert.Null(reservation.ActualCompletionTokens);
        }

        await using (var afterRecoveryDb = new ApplicationDbContext(dbOptions))
        {
            var manager = new AiTokenBudgetManager(afterRecoveryDb, options, time);
            Assert.False((await manager.ReserveAsync("feedback_analysis", Guid.NewGuid(), 1,
                801, default)).Succeeded);
            AiBudgetReservationResult releasable = await manager.ReserveAsync(
                "feedback_analysis", Guid.NewGuid(), 1, 800, default);
            Assert.True(releasable.Succeeded);
            await manager.ReleaseAsync(releasable.ReservationId!.Value, default);
            await manager.ReleaseAsync(releasable.ReservationId.Value, default);
        }
        await using (var finalDb = new ApplicationDbContext(dbOptions))
        {
            AiTokenBudgetPeriod period = await finalDb.AiTokenBudgetPeriods
                .OrderByDescending(item => item.PeriodStart).FirstAsync();
            Assert.Equal(0, period.ReservedTokens);
            Assert.Equal(700, period.EstimatedConsumedTokens);
            Assert.Equal(0, period.ActualTokens);
        }
    }

    private static async Task AssertPeriodAsync(DbContextOptions<ApplicationDbContext> options,
        int reserved, int estimated, int actual)
    {
        await using var verify = new ApplicationDbContext(options);
        AiTokenBudgetPeriod period = await verify.AiTokenBudgetPeriods.SingleAsync();
        Assert.Equal(reserved, period.ReservedTokens);
        Assert.Equal(estimated, period.EstimatedConsumedTokens);
        Assert.Equal(actual, period.ActualTokens);
    }

    private static async Task AssertLatestPeriodAsync(
        DbContextOptions<ApplicationDbContext> options, int reserved, int estimated, int actual)
    {
        await using var verify = new ApplicationDbContext(options);
        AiTokenBudgetPeriod period = await verify.AiTokenBudgetPeriods
            .OrderByDescending(item => item.PeriodStart).FirstAsync();
        Assert.Equal(reserved, period.ReservedTokens);
        Assert.Equal(estimated, period.EstimatedConsumedTokens);
        Assert.Equal(actual, period.ActualTokens);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
