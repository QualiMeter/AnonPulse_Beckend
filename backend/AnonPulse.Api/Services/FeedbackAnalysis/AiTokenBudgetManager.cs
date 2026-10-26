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
    Task ReconcileStaleAsync(CancellationToken cancellationToken);
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
            .AsNoTracking()
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
        int updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_token_budget_periods
            SET reserved_tokens = reserved_tokens + {estimatedTokens}
            WHERE period_start = {start}
              AND reserved_tokens + estimated_consumed_tokens + {estimatedTokens} <= token_limit
            """, cancellationToken);
        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Exhausted(now, end);
        }
        var created = NewReservation(workloadType, workloadId, attemptNumber, estimatedTokens,
            start, now);
        dbContext.AiTokenBudgetReservations.Add(created);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true, created.Id, false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.Entry(created).State = EntityState.Detached;
            duplicate = await dbContext.AiTokenBudgetReservations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.WorkloadType == workloadType &&
                    item.WorkloadId == workloadId && item.AttemptNumber == attemptNumber,
                    cancellationToken);
            if (duplicate is not null) return new(true, duplicate.Id, true);
            throw;
        }
    }

    public Task SettleAsync(Guid reservationId, int? promptTokens, int? completionTokens,
        bool usageUnknown, CancellationToken cancellationToken) => UpdateAsync(reservationId,
            release: false, promptTokens, completionTokens, usageUnknown, cancellationToken);

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken) =>
        UpdateAsync(reservationId, release: true, null, null, false, cancellationToken);

    public async Task ReconcileStaleAsync(CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        DateTime staleBefore = now - TimeSpan.FromSeconds(_options.TimeoutSeconds + 30);
        if (!dbContext.Database.IsRelational())
        {
            await InMemoryGate.WaitAsync(cancellationToken);
            try
            {
                AiTokenBudgetReservation[] stale = await dbContext.AiTokenBudgetReservations
                    .Where(item => item.Status == "reserved" && item.CreatedAt < staleBefore)
                    .ToArrayAsync(cancellationToken);
                foreach (AiTokenBudgetReservation reservation in stale)
                {
                    AiTokenBudgetPeriod period = await dbContext.AiTokenBudgetPeriods.SingleAsync(
                        item => item.PeriodStart == reservation.PeriodStart, cancellationToken);
                    period.ReservedTokens -= reservation.ReservedTokens;
                    period.EstimatedConsumedTokens += reservation.ReservedTokens;
                    reservation.ChargedTokens = reservation.ReservedTokens;
                    reservation.Status = "settled";
                    reservation.SettledAt = now;
                }
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                InMemoryGate.Release();
            }
            return;
        }

        DateTime[] periods = await dbContext.AiTokenBudgetReservations.AsNoTracking()
            .Where(item => item.Status == "reserved" && item.CreatedAt < staleBefore)
            .Select(item => item.PeriodStart).Distinct().ToArrayAsync(cancellationToken);
        foreach (DateTime periodStart in periods)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await LockPeriodAsync(periodStart, cancellationToken);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                WITH stale AS (
                    UPDATE ai_token_budget_reservations
                    SET status = 'settled', settled_at = {now},
                        charged_tokens = reserved_tokens
                    WHERE period_start = {periodStart} AND status = 'reserved'
                      AND created_at < {staleBefore}
                    RETURNING reserved_tokens
                ), total AS (
                    SELECT COALESCE(SUM(reserved_tokens), 0)::integer AS tokens FROM stale
                )
                UPDATE ai_token_budget_periods
                SET reserved_tokens = reserved_tokens - total.tokens,
                    estimated_consumed_tokens = estimated_consumed_tokens + total.tokens
                FROM total
                WHERE period_start = {periodStart} AND total.tokens > 0
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task UpdateAsync(Guid reservationId, bool release, int? promptTokens,
        int? completionTokens, bool usageUnknown, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        AiTokenBudgetReservation? reservation = dbContext.Database.IsRelational()
            ? await dbContext.AiTokenBudgetReservations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == reservationId, cancellationToken)
            : await dbContext.AiTokenBudgetReservations.SingleOrDefaultAsync(
                item => item.Id == reservationId, cancellationToken);
        if (reservation is null || reservation.Status != "reserved")
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return;
        }
        DateTime settledAt = timeProvider.GetUtcNow().UtcDateTime;
        int? actual = promptTokens is int prompt && completionTokens is int completion
            ? checked(prompt + completion) : null;
        int charged = usageUnknown || actual is null ? reservation.ReservedTokens : actual.Value;
        if (dbContext.Database.IsRelational())
        {
            await LockPeriodAsync(reservation.PeriodStart, cancellationToken);
            int changed = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ai_token_budget_reservations
                SET status = {(release ? "released" : "settled")}, settled_at = {settledAt},
                    actual_prompt_tokens = {(release ? null : promptTokens)},
                    actual_completion_tokens = {(release ? null : completionTokens)},
                    charged_tokens = {(release ? 0 : charged)}
                WHERE id = {reservationId} AND status = 'reserved'
                """, cancellationToken);
            if (changed != 0)
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE ai_token_budget_periods
                    SET reserved_tokens = reserved_tokens - {reservation.ReservedTokens},
                        estimated_consumed_tokens = estimated_consumed_tokens +
                            {(release ? 0 : charged)},
                        actual_tokens = actual_tokens + {(release ? 0 : actual ?? 0)}
                    WHERE period_start = {reservation.PeriodStart}
                    """, cancellationToken);
            await transaction!.CommitAsync(cancellationToken);
            return;
        }

        AiTokenBudgetPeriod period = await dbContext.AiTokenBudgetPeriods.SingleAsync(
            item => item.PeriodStart == reservation.PeriodStart, cancellationToken);
        period.ReservedTokens -= reservation.ReservedTokens;
        reservation.SettledAt = settledAt;
        if (release)
        {
            reservation.Status = "released";
        }
        else
        {
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

    private async Task LockPeriodAsync(DateTime periodStart,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_token_budget_periods
            SET reserved_tokens = reserved_tokens
            WHERE period_start = {periodStart}
            """, cancellationToken);

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
