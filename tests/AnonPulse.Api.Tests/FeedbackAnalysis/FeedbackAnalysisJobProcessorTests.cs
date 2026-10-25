using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AnonPulse.Api.Tests.FeedbackAnalysis;

public sealed class FeedbackAnalysisJobProcessorTests
{
    [Fact]
    public async Task IdentifiedFeedback_SendsOnlySanitizedTextAndOpaqueIds()
    {
        await using ApplicationDbContext db = CreateDb();
        (_, Poll poll, PollQuestion question) = FeedbackAnalysisServiceTests.Seed(db,
            PollStatus.Closed, TextQuestionPurpose.Feedback, PollResponseMode.Identified,
            "Я Иван, email ivan@example.org, телефон +7 (912) 123-45-67");
        PollFeedbackAnalysis analysis = FeedbackAnalysisServiceTests.AddAnalysis(db, poll.Id,
            question.Id, FeedbackAnalysisStatus.Processing);
        await db.SaveChangesAsync();
        var fake = new FakeProvider(input => Success(input));

        await CreateProcessor(db, fake).ProcessAsync(analysis.Id, default);

        FeedbackInputAnswer sent = Assert.Single(fake.LastInput!.Answers);
        Assert.DoesNotContain("ivan@example.org", sent.Text);
        Assert.DoesNotContain("912", sent.Text);
        Assert.Contains("[email удалён]", sent.Text);
        Assert.Contains("[телефон удалён]", sent.Text);
        Assert.DoesNotContain(question.TextAnswers.Single().Id.ToString(), sent.TemporaryId);
        Assert.DoesNotContain("Иван", sent.TemporaryId);
        Assert.Equal(FeedbackAnalysisStatus.Completed, analysis.Status);
    }

    [Fact]
    public async Task ThemeMentions_AreCountedFromUniqueValidatedTemporaryIds()
    {
        await using ApplicationDbContext db = CreateDb();
        (_, Poll poll, PollQuestion question) = FeedbackAnalysisServiceTests.Seed(db,
            PollStatus.Closed, TextQuestionPurpose.Feedback, PollResponseMode.Anonymous,
            "Больше практики", "Нужны практические задачи");
        PollFeedbackAnalysis analysis = FeedbackAnalysisServiceTests.AddAnalysis(db, poll.Id,
            question.Id, FeedbackAnalysisStatus.Processing);
        analysis.AnswerCount = 2;
        await db.SaveChangesAsync();
        var fake = new FakeProvider(input => Task.FromResult(new ProviderAnalysisResult(
            new ProviderReport("Итог", [new("Практика", "Нужно больше практики", "problem",
                [input.Answers[0].TemporaryId, input.Answers[1].TemporaryId, input.Answers[0].TemporaryId])],
                [], []), 10, 20)));

        await CreateProcessor(db, fake).ProcessAsync(analysis.Id, default);

        string json = analysis.ReportJson!;
        Assert.Contains("\"mentionCount\":2", json);
        Assert.DoesNotContain("answerIds", json);
        Assert.Equal(10, analysis.PromptTokens);
        Assert.Equal(20, analysis.CompletionTokens);
    }

    [Fact]
    public async Task UnknownAnswerId_FailsAsInvalidProviderResponse()
    {
        await using ApplicationDbContext db = CreateDb();
        (_, Poll poll, PollQuestion question) = FeedbackAnalysisServiceTests.Seed(db,
            PollStatus.Closed, TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Ответ");
        PollFeedbackAnalysis analysis = FeedbackAnalysisServiceTests.AddAnalysis(db, poll.Id,
            question.Id, FeedbackAnalysisStatus.Processing);
        await db.SaveChangesAsync();
        var fake = new FakeProvider(_ => Task.FromResult(new ProviderAnalysisResult(
            new ProviderReport("Итог", [new("Тема", "Описание", "problem", ["invented-id"])],
                [], []), null, null)));

        await CreateProcessor(db, fake).ProcessAsync(analysis.Id, default);

        Assert.Equal(FeedbackAnalysisStatus.Failed, analysis.Status);
        Assert.Equal("invalid_provider_response", analysis.ErrorCode);
    }

    [Fact]
    public async Task RateLimit_UsesRetryAfterAndStopsAtConfiguredAttemptLimit()
    {
        await using ApplicationDbContext db = CreateDb();
        (_, Poll poll, PollQuestion question) = FeedbackAnalysisServiceTests.Seed(db,
            PollStatus.Closed, TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Ответ");
        PollFeedbackAnalysis analysis = FeedbackAnalysisServiceTests.AddAnalysis(db, poll.Id,
            question.Id, FeedbackAnalysisStatus.Processing);
        await db.SaveChangesAsync();
        var fake = new FakeProvider(_ => throw new FeedbackProviderException(
            "free_quota_exhausted", true, TimeSpan.FromMinutes(2)));
        DateTime before = DateTime.UtcNow;

        await CreateProcessor(db, fake).ProcessAsync(analysis.Id, default);

        Assert.Equal(FeedbackAnalysisStatus.Pending, analysis.Status);
        Assert.True(analysis.NextAttemptAt >= before.AddMinutes(2));
        analysis.Status = FeedbackAnalysisStatus.Processing;
        analysis.Attempts = 3;
        await db.SaveChangesAsync();
        await CreateProcessor(db, fake).ProcessAsync(analysis.Id, default);
        Assert.Equal(FeedbackAnalysisStatus.Failed, analysis.Status);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task ProviderTimeout_IsRetriedButRemainsBounded()
    {
        await using ApplicationDbContext db = CreateDb();
        (_, Poll poll, PollQuestion question) = FeedbackAnalysisServiceTests.Seed(db,
            PollStatus.Closed, TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Ответ");
        PollFeedbackAnalysis analysis = FeedbackAnalysisServiceTests.AddAnalysis(db, poll.Id,
            question.Id, FeedbackAnalysisStatus.Processing);
        await db.SaveChangesAsync();

        await CreateProcessor(db, new NeverCompletingProvider()).ProcessAsync(analysis.Id, default);

        Assert.Equal(FeedbackAnalysisStatus.Pending, analysis.Status);
        Assert.Equal("provider_timeout", analysis.ErrorCode);
    }

    [Fact]
    public async Task CompletedResult_RemainsStoredAcrossNewDbContext()
    {
        string name = $"feedback-restart-{Guid.NewGuid()}";
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name).Options;
        Guid analysisId;
        await using (var db = new ApplicationDbContext(options))
        {
            (_, Poll poll, PollQuestion question) = FeedbackAnalysisServiceTests.Seed(db,
                PollStatus.Closed, TextQuestionPurpose.Feedback, PollResponseMode.Anonymous, "Ответ");
            PollFeedbackAnalysis analysis = FeedbackAnalysisServiceTests.AddAnalysis(db, poll.Id,
                question.Id, FeedbackAnalysisStatus.Processing);
            await db.SaveChangesAsync();
            analysisId = analysis.Id;
            await CreateProcessor(db, new FakeProvider(Success)).ProcessAsync(analysisId, default);
        }
        await using var restarted = new ApplicationDbContext(options);
        PollFeedbackAnalysis stored = await restarted.PollFeedbackAnalyses.SingleAsync(x => x.Id == analysisId);
        Assert.Equal(FeedbackAnalysisStatus.Completed, stored.Status);
        Assert.NotNull(stored.ReportJson);
    }

    private static Task<ProviderAnalysisResult> Success(FeedbackAnalysisInput input) =>
        Task.FromResult(new ProviderAnalysisResult(new ProviderReport("Резюме",
            [new("Тема", "Описание", "positive", [input.Answers[0].TemporaryId])],
            [new("Действие", "Описание", "medium")], ["Возможна неточность."]), 1, 1));

    private static FeedbackAnalysisJobProcessor CreateProcessor(ApplicationDbContext db,
        IFeedbackAnalysisProvider provider) => new(db, provider, new PersonalDataSanitizer(),
        Options.Create(new AiOptions { Enabled = true, MaxRetries = 2, TimeoutSeconds = 2 }),
        TimeProvider.System, NullLogger<FeedbackAnalysisJobProcessor>.Instance);

    private static ApplicationDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"feedback-worker-{Guid.NewGuid()}").Options);

    private sealed class FakeProvider(
        Func<FeedbackAnalysisInput, Task<ProviderAnalysisResult>> handler)
        : IFeedbackAnalysisProvider
    {
        public string Name => "Fake";
        public int Calls { get; private set; }
        public FeedbackAnalysisInput? LastInput { get; private set; }
        public Task<ProviderAnalysisResult> AnalyzeAsync(FeedbackAnalysisInput input,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastInput = input;
            return handler(input);
        }
    }

    private sealed class NeverCompletingProvider : IFeedbackAnalysisProvider
    {
        public string Name => "TimeoutFake";
        public async Task<ProviderAnalysisResult> AnalyzeAsync(FeedbackAnalysisInput input,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
