using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.FeedbackAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AnonPulse.Api.Tests.FeedbackAnalysis;

public sealed class FeedbackAnalysisServiceTests
{
    [Theory]
    [InlineData(PollResponseMode.Anonymous)]
    [InlineData(PollResponseMode.Identified)]
    public async Task ClosedFeedbackQuestion_CreatesOnePersistentJob(PollResponseMode mode)
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid owner, Poll poll, PollQuestion question) = Seed(db, PollStatus.Closed,
            TextQuestionPurpose.Feedback, mode, "Полезная обратная связь");
        var service = CreateService(db);

        FeedbackAnalysisServiceResult first = await service.StartAsync(owner, false,
            poll.Id, question.Id, true, default);
        FeedbackAnalysisServiceResult second = await service.StartAsync(owner, false,
            poll.Id, question.Id, true, default);

        Assert.Equal(FeedbackAnalysisOutcome.Succeeded, first.Outcome);
        Assert.Equal("pending", first.Analysis!.Status);
        Assert.Equal(first.Analysis.Id, second.Analysis!.Id);
        Assert.Single(db.PollFeedbackAnalyses);
        Assert.Equal(1, first.Analysis.AnswerCount);
    }

    [Fact]
    public async Task CompletedAnalysis_IsReturnedWithoutProviderInvocation()
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid owner, Poll poll, PollQuestion question) = Seed(db, PollStatus.Closed,
            TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Ответ");
        PollFeedbackAnalysis analysis = AddAnalysis(db, poll.Id, question.Id,
            FeedbackAnalysisStatus.Completed);
        analysis.ReportJson = """{"summary":"Итог","themes":[],"recommendations":[],"limitations":[]}""";
        await db.SaveChangesAsync();

        FeedbackAnalysisServiceResult result = await CreateService(db).GetAsync(owner, false,
            poll.Id, question.Id, default);

        Assert.Equal(FeedbackAnalysisOutcome.Succeeded, result.Outcome);
        Assert.Equal("Итог", result.Analysis!.Report!.Summary);
    }

    [Fact]
    public async Task OpenPoll_IsRejected()
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid owner, Poll poll, PollQuestion question) = Seed(db, PollStatus.Open,
            TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Ответ");
        FeedbackAnalysisServiceResult result = await CreateService(db).StartAsync(owner, false,
            poll.Id, question.Id, true, default);
        Assert.Equal("poll_not_closed", result.ErrorCode);
        Assert.Empty(db.PollFeedbackAnalyses);
    }

    [Fact]
    public async Task KnowledgeQuestion_IsRejectedWithDedicatedCode()
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid owner, Poll poll, PollQuestion question) = Seed(db, PollStatus.Closed,
            TextQuestionPurpose.Knowledge, PollResponseMode.Anonymous, "Ответ");
        FeedbackAnalysisServiceResult result = await CreateService(db).StartAsync(owner, false,
            poll.Id, question.Id, true, default);
        Assert.Equal("knowledge_analysis_not_supported", result.ErrorCode);
    }

    [Fact]
    public async Task OtherTeacher_IsForbidden_ButAdministratorIsAllowed()
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid _, Poll poll, PollQuestion question) = Seed(db, PollStatus.Closed,
            TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Ответ");
        FeedbackAnalysisService service = CreateService(db);
        FeedbackAnalysisServiceResult forbidden = await service.StartAsync(Guid.NewGuid(), false,
            poll.Id, question.Id, true, default);
        FeedbackAnalysisServiceResult admin = await service.StartAsync(Guid.NewGuid(), true,
            poll.Id, question.Id, true, default);
        Assert.Equal(FeedbackAnalysisOutcome.Forbidden, forbidden.Outcome);
        Assert.Equal(FeedbackAnalysisOutcome.Succeeded, admin.Outcome);
    }

    [Fact]
    public async Task ExternalProcessingRequiresExplicitBackendConfirmation()
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid owner, Poll poll, PollQuestion question) = Seed(db, PollStatus.Closed,
            TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Ответ");
        FeedbackAnalysisServiceResult result = await CreateService(db).StartAsync(owner, false,
            poll.Id, question.Id, false, default);
        Assert.Equal("external_processing_consent_required", result.ErrorCode);
    }

    [Fact]
    public async Task OversizedFeedbackSet_IsRejectedWithoutSilentTruncation()
    {
        await using ApplicationDbContext db = CreateDb();
        (Guid owner, Poll poll, PollQuestion question) = Seed(db, PollStatus.Closed,
            TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Первый", "Второй");
        FeedbackAnalysisService service = CreateService(db, new AiOptions
        {
            Enabled = true,
            MaxAnswers = 1,
            Groq = new GroqOptions { ApiKey = "fake-test-key" }
        });

        FeedbackAnalysisServiceResult result = await service.StartAsync(owner, false,
            poll.Id, question.Id, true, default);

        Assert.Equal(FeedbackAnalysisOutcome.TooLarge, result.Outcome);
        Assert.Equal("feedback_limit_exceeded", result.ErrorCode);
        Assert.Empty(db.PollFeedbackAnalyses);
    }

    private static FeedbackAnalysisService CreateService(
        ApplicationDbContext db, AiOptions? options = null) => new(
        db, Options.Create(options ?? new AiOptions
        {
            Enabled = true,
            Groq = new GroqOptions { ApiKey = "fake-test-key" }
        }), TimeProvider.System);

    private static ApplicationDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"feedback-service-{Guid.NewGuid()}").Options);

    internal static (Guid Owner, Poll Poll, PollQuestion Question) Seed(
        ApplicationDbContext db, PollStatus status, TextQuestionPurpose purpose,
        PollResponseMode mode, params string[] answers)
    {
        Guid owner = Guid.NewGuid();
        var user = new ApplicationUser { Id = owner, UserName = $"{owner}@test", Email = $"{owner}@test" };
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = owner,
            Creator = user,
            Status = status,
            ResponseMode = mode,
            CreatedAt = DateTime.UtcNow
        };
        var question = new PollQuestion
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Poll = poll,
            Text = "Что улучшить?",
            Type = PollQuestionType.Text,
            TextPurpose = purpose
        };
        poll.Questions.Add(question);
        db.AddRange(user, poll, question);
        foreach (string answerText in answers)
        {
            var answer = new PollTextAnswer
            {
                Id = Guid.NewGuid(),
                QuestionId = question.Id,
                Question = question,
                Text = answerText
            };
            question.TextAnswers.Add(answer);
            db.Add(answer);
        }
        db.SaveChanges();
        return (owner, poll, question);
    }

    internal static PollFeedbackAnalysis AddAnalysis(ApplicationDbContext db, Guid pollId,
        Guid questionId, FeedbackAnalysisStatus status)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        List<PollTextAnswer> answers = db.PollTextAnswers
            .Where(item => item.QuestionId == questionId)
            .OrderBy(item => item.Id).ToList();
        foreach (PollTextAnswer answer in answers)
            hash.AppendData(Encoding.UTF8.GetBytes(
                $"{answer.Id:N}:{answer.Text.Length}:{answer.Text}\n"));
        var analysis = new PollFeedbackAnalysis
        {
            Id = Guid.NewGuid(),
            PollId = pollId,
            QuestionId = questionId,
            Status = status,
            Provider = "Fake",
            Model = "fake",
            AlgorithmVersion = "feedback-v1",
            AnswerCount = answers.Count,
            AnswerFingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            Attempts = 1
        };
        db.Add(analysis);
        return analysis;
    }
}
