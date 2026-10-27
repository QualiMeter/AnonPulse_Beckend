using System.Data;
using System.Data.Common;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.KnowledgeGrading;

public interface IKnowledgeGradingJobQueue
{
    Task<KnowledgeGradingLease?> ClaimNextAsync(CancellationToken cancellationToken);
}

public sealed record KnowledgeGradingLease(Guid JobId, Guid LeaseId, int Generation);

public sealed class KnowledgeGradingJobQueue(
    ApplicationDbContext dbContext,
    IOptions<AiOptions> options,
    TimeProvider timeProvider) : IKnowledgeGradingJobQueue
{
    private readonly AiOptions _options = options.Value;

    public async Task<KnowledgeGradingLease?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        DateTime staleBefore = now - TimeSpan.FromSeconds(_options.TimeoutSeconds + 30);
        int maximumAttempts = _options.MaxRetries + 1;
        if (!dbContext.Database.IsRelational())
        {
            PollKnowledgeGradingJob? job = await dbContext.PollKnowledgeGradingJobs
                .Where(item => item.Status == KnowledgeGradingJobStatus.Pending &&
                               (item.NextAttemptAt == null || item.NextAttemptAt <= now))
                .OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (job is null) return null;
            Guid leaseId = Guid.NewGuid();
            job.Status = KnowledgeGradingJobStatus.Processing;
            job.StartedAt = now;
            job.AutomaticAttempts++;
            job.LeaseId = leaseId;
            job.LeaseGeneration++;
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(job.Id, leaseId, job.LeaseGeneration);
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE poll_knowledge_grading_jobs
            SET status = CASE WHEN automatic_attempts < {maximumAttempts} THEN 'pending' ELSE 'failed' END,
                next_attempt_at = CASE WHEN automatic_attempts < {maximumAttempts} THEN {now} ELSE NULL END,
                completed_at = CASE WHEN automatic_attempts < {maximumAttempts} THEN NULL ELSE {now} END,
                error_code = 'worker_interrupted', lease_id = NULL
            WHERE status = 'processing' AND started_at < {staleBefore}
            """, cancellationToken);
        DbConnection connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        Guid newLeaseId = Guid.NewGuid();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE poll_knowledge_grading_jobs
            SET status = 'processing', started_at = @now,
                automatic_attempts = automatic_attempts + 1,
                lease_id = @lease_id, lease_generation = lease_generation + 1
            WHERE id = (
                SELECT id FROM poll_knowledge_grading_jobs
                WHERE status = 'pending' AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
                ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            RETURNING id, lease_generation
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "now"; parameter.Value = now;
        command.Parameters.Add(parameter);
        DbParameter leaseParameter = command.CreateParameter();
        leaseParameter.ParameterName = "lease_id"; leaseParameter.Value = newLeaseId;
        command.Parameters.Add(leaseParameter);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), newLeaseId, reader.GetInt32(1)) : null;
    }
}
