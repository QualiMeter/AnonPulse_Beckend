using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.FeedbackAnalysis;
using AnonPulse.Api.Services.KnowledgeGrading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AnonPulse.Api.Tests.KnowledgeGrading;

public sealed class KnowledgeGradingServiceTests
{
    [Fact]
    public async Task Start_RequiresDedicatedFlagAndExplicitConfirmation()
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid owner, Poll poll, PollQuestion question, _) = Seed(db, PollStatus.Closed);
        KnowledgeGradingService disabled = Service(db, enabled: false);
        KnowledgeGradingServiceResult withoutConsent = await disabled.StartAsync(owner, false,
            poll.Id, question.Id, false, default);
        Assert.Equal(KnowledgeGradingOutcome.Invalid, withoutConsent.Outcome);
        Assert.Equal("external_processing_consent_required", withoutConsent.ErrorCode);

        KnowledgeGradingServiceResult result = await disabled.StartAsync(owner, false,
            poll.Id, question.Id, true, default);
        Assert.Equal(KnowledgeGradingOutcome.Disabled, result.Outcome);
        Assert.Empty(db.PollKnowledgeGradingJobs);
    }

    [Fact]
    public async Task Start_IsIdempotentAndSnapshotIsSanitized()
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid owner, Poll poll, PollQuestion question, _) = Seed(db, PollStatus.Closed,
            "Почта test@example.org, телефон +7 912 123-45-67");
        KnowledgeGradingService service = Service(db, enabled: true);

        KnowledgeGradingServiceResult first = await service.StartAsync(owner, false,
            poll.Id, question.Id, true, default);
        KnowledgeGradingServiceResult second = await service.StartAsync(owner, false,
            poll.Id, question.Id, true, default);

        Assert.Equal(KnowledgeGradingOutcome.Succeeded, first.Outcome);
        Assert.Equal(first.Job!.Id, second.Job!.Id);
        PollKnowledgeGradingJob job = Assert.Single(db.PollKnowledgeGradingJobs);
        Assert.DoesNotContain("test@example.org", job.AnswersSnapshotJson);
        Assert.DoesNotContain("912", job.AnswersSnapshotJson);
        Assert.Equal("openai/gpt-oss-20b", job.Model);
        Assert.Equal("knowledge-v1", job.AlgorithmVersion);
        Assert.True(job.ReservedInputTokens > 0);
    }

    [Fact]
    public async Task Start_RejectsOpenTestSurveyAndForeignTeacher()
    {
        await using ApplicationDbContext openDb = CreateDb();
        (Guid owner, Poll open, PollQuestion openQuestion, _) = Seed(openDb, PollStatus.Open);
        KnowledgeGradingServiceResult openResult = await Service(openDb, true).StartAsync(owner,
            false, open.Id, openQuestion.Id, true, default);
        Assert.Equal(KnowledgeGradingOutcome.Conflict, openResult.Outcome);

        await using ApplicationDbContext foreignDb = CreateDb();
        (_, Poll foreign, PollQuestion foreignQuestion, _) = Seed(foreignDb, PollStatus.Closed);
        KnowledgeGradingServiceResult foreignResult = await Service(foreignDb, true).StartAsync(
            Guid.NewGuid(), false, foreign.Id, foreignQuestion.Id, true, default);
        Assert.Equal(KnowledgeGradingOutcome.Forbidden, foreignResult.Outcome);

        await using ApplicationDbContext surveyDb = CreateDb();
        (Guid surveyOwner, Poll survey, PollQuestion surveyQuestion, _) = Seed(surveyDb,
            PollStatus.Closed);
        survey.Kind = PollKind.Survey;
        await surveyDb.SaveChangesAsync();
        KnowledgeGradingServiceResult surveyResult = await Service(surveyDb, true).StartAsync(
            surveyOwner, false, survey.Id, surveyQuestion.Id, true, default);
        Assert.Equal("poll_not_test", surveyResult.ErrorCode);
    }

    [Fact]
    public async Task AdministratorCanStartOwnersJob()
    {
        await using ApplicationDbContext db = CreateDb();
        (_, Poll poll, PollQuestion question, _) = Seed(db, PollStatus.Closed);
        KnowledgeGradingServiceResult result = await Service(db, true).StartAsync(Guid.NewGuid(),
            true, poll.Id, question.Id, true, default);
        Assert.Equal(KnowledgeGradingOutcome.Succeeded, result.Outcome);
    }

    private static KnowledgeGradingService Service(ApplicationDbContext db, bool enabled) => new(
        db, new PersonalDataSanitizer(), Options.Create(new AiOptions
        {
            Enabled = true,
            KnowledgeGradingEnabled = enabled,
            Provider = "Groq",
            Model = "openai/gpt-oss-20b",
            KnowledgeMaxAnswers = 50,
            KnowledgeMaxTotalCharacters = 20_000,
            KnowledgeMaxInputTokens = 6_000,
            KnowledgeMaxOutputTokens = 2_000,
            KnowledgeMaxReservedTokens = 8_000,
            Groq = new GroqOptions { ApiKey = "fake-test-key" }
        }), TimeProvider.System);

    private static (Guid, Poll, PollQuestion, NamedAttemptAnswer) Seed(
        ApplicationDbContext db, PollStatus status, string text = "Полиморфизм — общий интерфейс")
    {
        Guid owner = Guid.NewGuid();
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = owner,
            Kind = PollKind.Test,
            ResponseMode = PollResponseMode.Identified,
            Status = status,
            TestScale = 5,
            CreatedAt = DateTime.UtcNow
        };
        var question = new PollQuestion
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Poll = poll,
            Text = "Что такое полиморфизм?",
            Position = 0,
            Type = PollQuestionType.Text,
            TextPurpose = TextQuestionPurpose.Knowledge
        };
        question.KnowledgeSettings = new PollQuestionKnowledgeSettings
        {
            QuestionId = question.Id,
            Question = question,
            ExpectedAnswer = "Общий интерфейс",
            GradingCriteria = "Определение и пример",
            MaxScore = 5
        };
        poll.Questions.Add(question);
        var attempt = new NamedAttempt
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Poll = poll,
            RespondentName = "Ученик",
            RespondentGroup = "A",
            ClientRequestId = Guid.NewGuid(),
            RequestHash = new string('a', 64),
            AccessSecretHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            MaximumScore = 5,
            RawScore = 0,
            IsFullyGraded = false,
            IsFinal = false
        };
        var answer = new NamedAttemptAnswer
        {
            Id = Guid.NewGuid(),
            AttemptId = attempt.Id,
            Attempt = attempt,
            QuestionId = question.Id,
            Question = question,
            Text = text,
            GradingState = TestAnswerGradingStates.Pending,
            MaximumScore = 5
        };
        attempt.Answers.Add(answer);
        db.AddRange(poll, attempt);
        db.SaveChanges();
        return (owner, poll, question, answer);
    }

    private static ApplicationDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"knowledge-service-{Guid.NewGuid()}").Options);
}
