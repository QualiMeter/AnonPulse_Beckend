using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.Tests;

public sealed class TestSubmissionService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<Services.Voting.TextAnswerOptions> textOptions) : ITestSubmissionService
{
    public async Task<TestSubmissionResult> SubmitAsync(
        Guid pollId,
        SubmitTestRequest request,
        CancellationToken cancellationToken = default)
    {
        string? name = NormalizeIdentity(request.RespondentName);
        string? group = NormalizeIdentity(request.RespondentGroup);
        Dictionary<string, string[]>? shapeErrors = ValidateShape(request, name, group);
        if (shapeErrors is not null)
        {
            return Invalid(shapeErrors);
        }

        string requestHash = ComputeRequestHash(name!, group!, request.Answers!);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Poll? poll = await dbContext.Polls
                .FromSqlInterpolated(
                    $"SELECT p.*, p.xmin FROM polls AS p WHERE p.id = {pollId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (poll is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new TestSubmissionResult(TestSubmissionOutcome.NotFound);
            }
            if (poll.Kind != PollKind.Test)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict("Poll is not a test.", "not_a_test");
            }
            NamedAttempt? existing = await dbContext.NamedAttempts.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.PollId == pollId &&
                            item.ClientRequestId == request.ClientRequestId,
                    cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return existing.RequestHash == requestHash
                    ? Conflict(
                        "The submission was already stored. The access secret cannot be recovered; " +
                        "use a new clientRequestId to submit a new logical attempt.",
                        "submission_already_stored_secret_unavailable")
                    : Conflict(
                        "ClientRequestId was already used with different submission content.",
                        "idempotency_key_reused");
            }
            if (poll.Status != PollStatus.Open)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict("Test is not open for submissions.", "test_not_open");
            }

            PollQuestion[] questions = await dbContext.PollQuestions.AsNoTracking()
                .Where(question => question.PollId == pollId)
                .Include(question => question.Options)
                .Include(question => question.CorrectOptions)
                .Include(question => question.KnowledgeSettings)
                .OrderBy(question => question.Position)
                .ToArrayAsync(cancellationToken);
            AnswerValidation validated = ValidateAnswers(
                request.Answers!, questions, textOptions.Value.MaximumLength);
            if (validated.Errors.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Invalid(validated.Errors);
            }

            string secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            DateTime now = timeProvider.GetUtcNow().UtcDateTime;
            var attempt = new NamedAttempt
            {
                Id = Guid.NewGuid(),
                PollId = pollId,
                RespondentName = name!,
                RespondentGroup = group!,
                ClientRequestId = request.ClientRequestId,
                RequestHash = requestHash,
                AccessSecretHash = Sha256Hex(secret),
                State = NamedAttemptStates.Completed,
                CreatedAt = now,
                CompletedAt = now,
                MaximumScore = questions.Sum(GetMaximumScore),
                IsFinal = false
            };

            foreach (PollQuestion question in questions)
            {
                ValidatedAnswer answer = validated.ByQuestion[question.Id];
                decimal maxScore = GetMaximumScore(question);
                var stored = new NamedAttemptAnswer
                {
                    Id = Guid.NewGuid(),
                    AttemptId = attempt.Id,
                    QuestionId = question.Id,
                    Text = answer.Text,
                    MaximumScore = maxScore
                };
                if (question.Type == PollQuestionType.Text)
                {
                    stored.GradingState = TestAnswerGradingStates.Pending;
                    stored.Score = null;
                }
                else
                {
                    HashSet<Guid> expected = question.CorrectOptions
                        .Select(item => item.OptionId).ToHashSet();
                    bool correct = expected.SetEquals(answer.OptionIds);
                    stored.GradingState = TestAnswerGradingStates.AutoGraded;
                    stored.Score = correct ? maxScore : 0m;
                    foreach (Guid optionId in answer.OptionIds)
                    {
                        stored.SelectedOptions.Add(new NamedAttemptAnswerOption
                        {
                            AnswerId = stored.Id,
                            OptionId = optionId
                        });
                    }
                }
                attempt.Answers.Add(stored);
            }

            TestScoreCalculator.Recalculate(attempt, poll.TestScale!.Value);
            dbContext.NamedAttempts.Add(attempt);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TestSubmissionResult(
                TestSubmissionOutcome.Succeeded,
                new TestSubmissionCreatedResponse(attempt.Id, secret));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private Dictionary<string, string[]>? ValidateShape(
        SubmitTestRequest request,
        string? name,
        string? group)
    {
        var errors = new Dictionary<string, string[]>();
        if (name is null || name.Length > 150)
        {
            errors[nameof(request.RespondentName)] =
                ["RespondentName must contain 1 to 150 characters."];
        }
        if (group is null || group.Length > 100)
        {
            errors[nameof(request.RespondentGroup)] =
                ["RespondentGroup must contain 1 to 100 characters."];
        }
        if (request.ClientRequestId == Guid.Empty)
        {
            errors[nameof(request.ClientRequestId)] = ["ClientRequestId is required."];
        }
        if (request.Answers is null || request.Answers.Count == 0)
        {
            errors[nameof(request.Answers)] = ["At least one answer is required."];
        }
        else if (request.Answers.Count > textOptions.Value.MaximumAnswersPerRequest)
        {
            errors[nameof(request.Answers)] = ["Too many answers were submitted."];
        }
        if (request.Answers is not null)
        {
            for (int index = 0; index < request.Answers.Count; index++)
            {
                TestAnswerRequest? answer = request.Answers[index];
                if (answer is null)
                {
                    errors[$"Answers[{index}]"] = ["Answer must not be null."];
                }
                else if (answer.QuestionId == Guid.Empty)
                {
                    errors[$"Answers[{index}].QuestionId"] = ["QuestionId is required."];
                }
            }
        }
        return errors.Count == 0 ? null : errors;
    }

    private static AnswerValidation ValidateAnswers(
        IReadOnlyList<TestAnswerRequest?> answers,
        IReadOnlyCollection<PollQuestion> questions,
        int maximumTextLength)
    {
        var errors = new Dictionary<string, string[]>();
        Dictionary<Guid, PollQuestion> questionById = questions.ToDictionary(item => item.Id);
        var byQuestion = new Dictionary<Guid, ValidatedAnswer>();
        for (int index = 0; index < answers.Count; index++)
        {
            TestAnswerRequest answer = answers[index]!;
            if (!questionById.TryGetValue(answer.QuestionId, out PollQuestion? question))
            {
                errors[$"Answers[{index}].QuestionId"] =
                    ["Question does not belong to this test."];
                continue;
            }
            if (!byQuestion.TryGetValue(question.Id, out ValidatedAnswer? collected))
            {
                collected = new ValidatedAnswer([], null);
                byQuestion.Add(question.Id, collected);
            }
            if (question.Type == PollQuestionType.Text)
            {
                if (collected.Text is not null)
                {
                    errors[$"Answers[{index}]"] = ["Only one text answer is allowed."];
                }
                if (answer.OptionId is not null)
                {
                    errors[$"Answers[{index}].OptionId"] =
                        ["OptionId is not allowed for a text question."];
                }
                string text = answer.Text?.Trim() ?? string.Empty;
                if (text.Length == 0 || text.Length > maximumTextLength)
                {
                    errors[$"Answers[{index}].Text"] =
                        [$"Text must contain 1 to {maximumTextLength} characters."];
                }
                byQuestion[question.Id] = collected with { Text = text };
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(answer.Text))
                {
                    errors[$"Answers[{index}].Text"] =
                        ["Text is not allowed for a choice question."];
                }
                if (answer.OptionId is null || answer.OptionId == Guid.Empty ||
                    question.Options.All(option => option.Id != answer.OptionId))
                {
                    errors[$"Answers[{index}].OptionId"] =
                        ["Option does not belong to the specified question."];
                }
                else if (!collected.OptionIds.Add(answer.OptionId.Value))
                {
                    errors[$"Answers[{index}].OptionId"] =
                        ["The same option may not be selected more than once."];
                }
            }
        }
        foreach (PollQuestion question in questions)
        {
            if (!byQuestion.TryGetValue(question.Id, out ValidatedAnswer? answer))
            {
                errors[$"Questions[{question.Id}]"] = ["Question must be answered."];
                continue;
            }
            if (question.Type == PollQuestionType.Text && string.IsNullOrWhiteSpace(answer.Text))
            {
                errors[$"Questions[{question.Id}]"] = ["Question must be answered."];
            }
            else if (question.Type == PollQuestionType.SingleChoice && answer.OptionIds.Count != 1)
            {
                errors[$"Questions[{question.Id}]"] =
                    ["Exactly one option must be selected."];
            }
            else if (question.Type == PollQuestionType.MultipleChoice && answer.OptionIds.Count == 0)
            {
                errors[$"Questions[{question.Id}]"] =
                    ["At least one option must be selected."];
            }
        }
        return new AnswerValidation(byQuestion, errors);
    }

    private static decimal GetMaximumScore(PollQuestion question) =>
        question.Type == PollQuestionType.Text
            ? question.KnowledgeSettings!.MaxScore
            : question.TestWeight!.Value;

    private static string? NormalizeIdentity(string? value)
    {
        if (value is null) return null;
        string normalized = string.Join(' ', value.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized;
    }

    private static string ComputeRequestHash(
        string name,
        string group,
        IReadOnlyList<TestAnswerRequest?> answers)
    {
        var canonical = new
        {
            name,
            group,
            answers = answers.Cast<TestAnswerRequest>()
                .Select(answer => new
                {
                    answer.QuestionId,
                    answer.OptionId,
                    Text = answer.Text?.Trim()
                })
                .OrderBy(answer => answer.QuestionId)
                .ThenBy(answer => answer.OptionId)
                .ThenBy(answer => answer.Text, StringComparer.Ordinal)
                .ToArray()
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static TestSubmissionResult Invalid(
        IReadOnlyDictionary<string, string[]> errors) =>
        new(TestSubmissionOutcome.Invalid, ValidationErrors: errors);

    private static TestSubmissionResult Conflict(string error, string code) =>
        new(TestSubmissionOutcome.Conflict, Error: error, Code: code);

    private sealed record ValidatedAnswer(HashSet<Guid> OptionIds, string? Text);
    private sealed record AnswerValidation(
        IReadOnlyDictionary<Guid, ValidatedAnswer> ByQuestion,
        IReadOnlyDictionary<string, string[]> Errors);
}
