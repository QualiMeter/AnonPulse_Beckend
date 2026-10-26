using System.Data;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public interface IAiTokenBudgetManager
{
    Task<AiBudgetReservationResult> ReserveAsync(string workloadType, Guid workloadId,
        int attemptNumber, int estimatedTokens, CancellationToken cancellationToken);
    Task SettleAsync(Guid reservationId, int? promptTokens, int? completionTokens,
        bool usageUnknown, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken);
}

public sealed record AiBudgetReservationResult(bool Succeeded, Guid? ReservationId,
    bool AlreadyExists, int? RetryAfterSeconds = null);

public sealed class AiTokenBudgetManager(
    ApplicationDbContext dbContext,
    IOptions<AiOptions> options,
    TimeProvider timeProvider) : IAiTokenBudgetManager
{
    private static readonly SemaphoreSlim InMemoryGate = new(1, 1);
    private readonly AiOptions _options = options.Value;

    public async Task<AiBudgetReservationResult> ReserveAsync(string workloadType,
        Guid workloadId, int attemptNumber, int estimatedTokens,
        CancellationToken cancellationToken)
    {
        if (estimatedTokens <= 0) throw new ArgumentOutOfRangeException(nameof(estimatedTokens));
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        (DateTime start, DateTime end) = GetPeriod(now);
        if (!dbContext.Database.IsRelational())
        {
            await InMemoryGate.WaitAsync(cancellationToken);
            try
            {
                AiTokenBudgetReservation? existing = await dbContext.AiTokenBudgetReservations
                    .SingleOrDefaultAsync(item => item.WorkloadType == workloadType &&
                        item.WorkloadId == workloadId && item.AttemptNumber == attemptNumber,
                        cancellationToken);
                if (existing is not null)
                    return new(true, existing.Id, true);
                AiTokenBudgetPeriod? period = await dbContext.AiTokenBudgetPeriods
                    .SingleOrDefaultAsync(item => item.PeriodStart == start, cancellationToken);
                if (period is null)
                {
                    period = NewPeriod(start, end);
                    dbContext.AiTokenBudgetPeriods.Add(period);
                }
                if (period.ReservedTokens + period.EstimatedConsumedTokens + estimatedTokens >
                    period.TokenLimit)
                    return Exhausted(now, end);
                period.ReservedTokens += estimatedTokens;
                var reservation = NewReservation(workloadType, workloadId, attemptNumber,
                    estimatedTokens, start, now);
                dbContext.AiTokenBudgetReservations.Add(reservation);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new(true, reservation.Id, false);
            }
            finally
            {
                InMemoryGate.Release();
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        AiTokenBudgetReservation? duplicate = await dbContext.AiTokenBudgetReservations
            .SingleOrDefaultAsync(item => item.WorkloadType == workloadType &&
                item.WorkloadId == workloadId && item.AttemptNumber == attemptNumber,
                cancellationToken);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(true, duplicate.Id, true);
        }
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_token_budget_periods
                (period_start, period_end, token_limit, reserved_tokens,
                 estimated_consumed_tokens, actual_tokens)
            VALUES ({start}, {end}, {_options.SharedTokenBudget}, 0, 0, 0)
            ON CONFLICT (period_start) DO NOTHING
            """, cancellationToken);
        AiTokenBudgetPeriod periodRow = await dbContext.AiTokenBudgetPeriods
            .FromSqlInterpolated($"""
                SELECT * FROM ai_token_budget_periods
                WHERE period_start = {start}
                FOR UPDATE
                """).SingleAsync(cancellationToken);
        // The period row serializes reservations. Recheck the idempotency key after taking
        // the lock because a concurrent transaction may have committed it while we waited.
        duplicate = await dbContext.AiTokenBudgetReservations
            .SingleOrDefaultAsync(item => item.WorkloadType == workloadType &&
                item.WorkloadId == workloadId && item.AttemptNumber == attemptNumber,
                cancellationToken);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(true, duplicate.Id, true);
        }
        if (periodRow.ReservedTokens + periodRow.EstimatedConsumedTokens + estimatedTokens >
            periodRow.TokenLimit)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Exhausted(now, end);
        }
        periodRow.ReservedTokens += estimatedTokens;
        var created = NewReservation(workloadType, workloadId, attemptNumber, estimatedTokens,
            start, now);
        dbContext.AiTokenBudgetReservations.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, created.Id, false);
    }

    public Task SettleAsync(Guid reservationId, int? promptTokens, int? completionTokens,
        bool usageUnknown, CancellationToken cancellationToken) => UpdateAsync(reservationId,
            release: false, promptTokens, completionTokens, usageUnknown, cancellationToken);

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken) =>
        UpdateAsync(reservationId, release: true, null, null, false, cancellationToken);

    private async Task UpdateAsync(Guid reservationId, bool release, int? promptTokens,
        int? completionTokens, bool usageUnknown, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        AiTokenBudgetReservation? reservation = dbContext.Database.IsRelational()
            ? await dbContext.AiTokenBudgetReservations.FromSqlInterpolated($"""
                SELECT * FROM ai_token_budget_reservations
                WHERE id = {reservationId}
                FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await dbContext.AiTokenBudgetReservations.SingleOrDefaultAsync(
                item => item.Id == reservationId, cancellationToken);
        if (reservation is null || reservation.Status != "reserved")
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return;
        }
        AiTokenBudgetPeriod period = dbContext.Database.IsRelational()
            ? await dbContext.AiTokenBudgetPeriods.FromSqlInterpolated($"""
                SELECT * FROM ai_token_budget_periods
                WHERE period_start = {reservation.PeriodStart}
                FOR UPDATE
                """).SingleAsync(cancellationToken)
            : await dbContext.AiTokenBudgetPeriods.SingleAsync(
                item => item.PeriodStart == reservation.PeriodStart, cancellationToken);
        period.ReservedTokens -= reservation.ReservedTokens;
        reservation.SettledAt = timeProvider.GetUtcNow().UtcDateTime;
        if (release)
        {
            reservation.Status = "released";
        }
        else
        {
            int? actual = promptTokens is int prompt && completionTokens is int completion
                ? checked(prompt + completion) : null;
            int charged = usageUnknown || actual is null ? reservation.ReservedTokens : actual.Value;
            period.EstimatedConsumedTokens += charged;
            if (actual is int actualTokens) period.ActualTokens += actualTokens;
            reservation.ActualPromptTokens = promptTokens;
            reservation.ActualCompletionTokens = completionTokens;
            reservation.ChargedTokens = charged;
            reservation.Status = "settled";
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private (DateTime Start, DateTime End) GetPeriod(DateTime now)
    {
        long seconds = checked(_options.SharedTokenBudgetPeriodMinutes * 60L);
        long unix = new DateTimeOffset(now).ToUnixTimeSeconds();
        long startUnix = unix - (unix % seconds);
        DateTime start = DateTimeOffset.FromUnixTimeSeconds(startUnix).UtcDateTime;
        return (start, start.AddSeconds(seconds));
    }

    private AiTokenBudgetPeriod NewPeriod(DateTime start, DateTime end) => new()
    {
        PeriodStart = start,
        PeriodEnd = end,
        TokenLimit = _options.SharedTokenBudget
    };

    private static AiTokenBudgetReservation NewReservation(string workloadType,
        Guid workloadId, int attemptNumber, int estimatedTokens, DateTime periodStart,
        DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            PeriodStart = periodStart,
            WorkloadType = workloadType,
            WorkloadId = workloadId,
            AttemptNumber = attemptNumber,
            ReservedTokens = estimatedTokens,
            Status = "reserved",
            CreatedAt = now
        };

    private static AiBudgetReservationResult Exhausted(DateTime now, DateTime end) =>
        new(false, null, false,
            Math.Max(1, (int)Math.Ceiling((end - now).TotalSeconds)));
}
