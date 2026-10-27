using System.Data;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Tests;

public sealed class TestAttemptService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider)
    : ITestAttemptService
{
    public async Task<TestAttemptsResult> GetAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var header = await dbContext.Polls.AsNoTracking()
            .Where(poll => poll.Id == pollId)
            .Select(poll => new { poll.CreatorId, poll.Status, poll.Kind })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null || (!isAdmin && header.CreatorId != actorId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new TestAttemptsResult(TestAttemptsOutcome.NotFound);
        }
        if (header.Kind != PollKind.Test)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("Named test attempts are available only for tests.");
        }
        if (header.Status != PollStatus.Closed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("Test attempts are available only after the test is closed.");
        }

        IQueryable<NamedAttempt> query = dbContext.NamedAttempts.AsNoTracking()
            .Where(attempt => attempt.PollId == pollId);
        int totalCount = await query.CountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        NamedAttempt[] attempts = offset >= totalCount ? [] : await query
            .Include(attempt => attempt.Answers)
            .ThenInclude(answer => answer.SelectedOptions)
            .OrderBy(attempt => attempt.CreatedAt)
            .ThenBy(attempt => attempt.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .AsSplitQuery()
            .ToArrayAsync(cancellationToken);
        TestAttemptResponse[] items = attempts.Select(attempt => new TestAttemptResponse(
            attempt.Id,
            attempt.RespondentName,
            attempt.RespondentGroup,
            attempt.State,
            attempt.CreatedAt,
            attempt.CompletedAt,
            attempt.RawScore,
            attempt.MaximumScore,
            attempt.NormalizedScore,
            attempt.IsFullyGraded,
            attempt.IsFinal,
            attempt.Answers.OrderBy(answer => answer.QuestionId)
                .Select(answer => new TestAttemptAnswerResponse(
                    answer.QuestionId,
                    answer.SelectedOptions.Select(item => item.OptionId).Order().ToArray(),
                    answer.Text,
                    answer.GradingState,
                    answer.Score,
                    answer.MaximumScore,
                    answer.Id))
                .ToArray()))
            .ToArray();
        await transaction.CommitAsync(cancellationToken);
        return new TestAttemptsResult(
            TestAttemptsOutcome.Succeeded,
            new PagedResponse<TestAttemptResponse>(
                items, page, pageSize, totalCount,
                totalCount == 0 ? 0 : ((totalCount - 1) / pageSize) + 1));
    }

    private static TestAttemptsResult Conflict(string error) =>
        new(TestAttemptsOutcome.Conflict, Error: error);

    public async Task<TestAttemptOperationResult> GradeAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        Guid attemptId,
        Guid answerId,
        GradeTestAnswerRequest request,
        CancellationToken cancellationToken = default)
    {
        if ((request.Score is null) == (request.SuggestionId is null))
        {
            return Invalid("grade_source_invalid",
                "Provide exactly one of score or suggestionId.", nameof(request.Score));
        }
        if (request.Score is decimal requestedScore && !IsValidPrecision(requestedScore))
        {
            return Invalid("score_precision_invalid",
                "Score must have no more than two decimal places.", nameof(request.Score));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            PollHeader? poll = await GetAuthorizedPollAsync(
                actorId, isAdmin, pollId, cancellationToken);
            TestAttemptOperationResult? rejected = ValidateClosedTest(poll);
            if (rejected is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return rejected;
            }
            NamedAttempt? attempt = await LockAttemptAsync(
                pollId, attemptId, cancellationToken);
            if (attempt is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }
            if (attempt.IsFinal)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ConflictOperation("attempt_finalized",
                    "A finalized attempt cannot be changed.");
            }

            await dbContext.Entry(attempt).Collection(item => item.Answers)
                .LoadAsync(cancellationToken);
            NamedAttemptAnswer? answer = attempt.Answers.SingleOrDefault(item =>
                item.Id == answerId);
            if (answer is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }

            decimal newScore;
            string source;
            Guid? suggestionId = null;
            if (request.SuggestionId is Guid requestedSuggestionId)
            {
                PollKnowledgeGradingSuggestion? suggestion =
                    await dbContext.PollKnowledgeGradingSuggestions.AsNoTracking()
                        .Include(item => item.Job)
                        .SingleOrDefaultAsync(item =>
                            item.Id == requestedSuggestionId && item.AnswerId == answerId &&
                            item.Job.PollId == pollId &&
                            item.Job.Status == KnowledgeGradingJobStatus.Completed,
                            cancellationToken);
                if (suggestion is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return NotFound();
                }
                newScore = suggestion.SuggestedScore;
                source = TestGradeChangeSources.AiSuggestion;
                suggestionId = suggestion.Id;
            }
            else
            {
                newScore = request.Score!.Value;
                source = TestGradeChangeSources.Manual;
            }
            if (newScore < 0m || newScore > answer.MaximumScore)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Invalid("score_out_of_range",
                    $"Score must be between 0 and {answer.MaximumScore}.",
                    nameof(request.Score));
            }

            decimal? oldScore = answer.Score;
            answer.Score = newScore;
            answer.GradingState = TestAnswerGradingStates.TeacherGraded;
            DateTime now = PostgreSqlTimestamp(timeProvider.GetUtcNow().UtcDateTime);
            dbContext.TestGradeChanges.Add(new TestGradeChange
            {
                Id = Guid.NewGuid(),
                AttemptId = attempt.Id,
                AnswerId = answer.Id,
                ActorId = actorId,
                OldScore = oldScore,
                NewScore = newScore,
                Source = source,
                KnowledgeGradingSuggestionId = suggestionId,
                ChangedAt = now
            });
            TestScoreCalculator.Recalculate(attempt, poll!.TestScale);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TestAttemptOperationResult(TestAttemptOperationOutcome.Succeeded,
                Grade: new GradeTestAnswerResponse(
                    answer.Id, answer.Score.Value, answer.GradingState));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TestAttemptOperationResult> FinalizeAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            PollHeader? poll = await GetAuthorizedPollAsync(
                actorId, isAdmin, pollId, cancellationToken);
            TestAttemptOperationResult? rejected = ValidateClosedTest(poll);
            if (rejected is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return rejected;
            }
            NamedAttempt? attempt = await LockAttemptAsync(
                pollId, attemptId, cancellationToken);
            if (attempt is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }
            await dbContext.Entry(attempt).Collection(item => item.Answers)
                .LoadAsync(cancellationToken);
            if (!attempt.IsFinal)
            {
                if (attempt.Answers.Any(answer =>
                        answer.GradingState == TestAnswerGradingStates.Pending ||
                        answer.Score is null))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return ConflictOperation("attempt_has_pending_answers",
                        "Every answer must have a final score before finalization.");
                }
                TestScoreCalculator.Recalculate(attempt, poll!.TestScale);
                attempt.FinalizedByUserId = actorId;
                attempt.FinalizedAt = PostgreSqlTimestamp(
                    timeProvider.GetUtcNow().UtcDateTime);
                attempt.IsFinal = true;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            FinalizeTestAttemptResponse response = ToFinalizationResponse(attempt);
            await transaction.CommitAsync(cancellationToken);
            return new TestAttemptOperationResult(TestAttemptOperationOutcome.Succeeded,
                Finalization: response);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TestAttemptOperationResult> GetHistoryAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        PollHeader? poll = await GetAuthorizedPollAsync(
            actorId, isAdmin, pollId, cancellationToken);
        TestAttemptOperationResult? rejected = ValidateClosedTest(poll);
        if (rejected is not null) return rejected;
        NamedAttempt? attempt = await dbContext.NamedAttempts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == attemptId && item.PollId == pollId,
                cancellationToken);
        if (attempt is null) return NotFound();
        TestGradeChangeResponse[] changes = await dbContext.TestGradeChanges.AsNoTracking()
            .Where(item => item.AttemptId == attemptId)
            .OrderBy(item => item.ChangedAt)
            .ThenBy(item => item.Id)
            .Select(item => new TestGradeChangeResponse(
                item.Id,
                item.AttemptId,
                item.AnswerId,
                item.ActorId,
                item.OldScore,
                item.NewScore,
                item.Source,
                item.KnowledgeGradingSuggestionId,
                item.ChangedAt))
            .ToArrayAsync(cancellationToken);
        return new TestAttemptOperationResult(TestAttemptOperationOutcome.Succeeded,
            History: new TestAttemptGradeHistoryResponse(
                attempt.Id, changes, attempt.FinalizedByUserId, attempt.FinalizedAt));
    }

    private async Task<PollHeader?> GetAuthorizedPollAsync(Guid actorId, bool isAdmin,
        Guid pollId, CancellationToken cancellationToken) =>
        await dbContext.Polls.AsNoTracking()
            .Where(poll => poll.Id == pollId && (isAdmin || poll.CreatorId == actorId))
            .Select(poll => new PollHeader(poll.Status, poll.Kind, poll.TestScale ?? 0))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<NamedAttempt?> LockAttemptAsync(Guid pollId, Guid attemptId,
        CancellationToken cancellationToken) =>
        await dbContext.NamedAttempts.FromSqlInterpolated(
                $"SELECT a.* FROM named_attempts AS a WHERE a.id = {attemptId} AND a.poll_id = {pollId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static TestAttemptOperationResult? ValidateClosedTest(PollHeader? poll)
    {
        if (poll is null) return NotFound();
        if (poll.Kind != PollKind.Test)
            return ConflictOperation("not_a_test", "This operation is available only for tests.");
        return poll.Status == PollStatus.Closed
            ? null
            : ConflictOperation("test_not_closed",
                "This operation is available only after the test is closed.");
    }

    private static bool IsValidPrecision(decimal score) =>
        decimal.Round(score, 2, MidpointRounding.ToZero) == score;

    private static DateTime PostgreSqlTimestamp(DateTime value) =>
        new(value.Ticks / 10 * 10, DateTimeKind.Utc);

    private static FinalizeTestAttemptResponse ToFinalizationResponse(NamedAttempt attempt) =>
        new(attempt.Id, attempt.RawScore, attempt.MaximumScore,
            attempt.NormalizedScore!.Value, attempt.IsFullyGraded, attempt.IsFinal,
            attempt.FinalizedByUserId!.Value, attempt.FinalizedAt!.Value);

    private static TestAttemptOperationResult NotFound() =>
        new(TestAttemptOperationOutcome.NotFound, ErrorCode: "test_attempt_not_found",
            Error: "Test attempt was not found.");

    private static TestAttemptOperationResult ConflictOperation(string code, string error) =>
        new(TestAttemptOperationOutcome.Conflict, ErrorCode: code, Error: error);

    private static TestAttemptOperationResult Invalid(string code, string error,
        string field) => new(TestAttemptOperationOutcome.Invalid, ErrorCode: code,
            Error: error, ValidationErrors: new Dictionary<string, string[]>
            {
                [field] = [error]
            });

    private sealed record PollHeader(PollStatus Status, PollKind Kind, int TestScale);
}
