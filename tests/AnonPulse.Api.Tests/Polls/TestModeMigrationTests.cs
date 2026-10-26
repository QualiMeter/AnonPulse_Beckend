using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using System.Text.Json;
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
        Guid anonymousQuestion = Guid.NewGuid();
        Guid anonymousOption = Guid.NewGuid();
        Guid anonymousVote = Guid.NewGuid();
        Guid anonymousParticipation = Guid.NewGuid();
        Guid identifiedChoiceQuestion = Guid.NewGuid();
        Guid identifiedTextQuestion = Guid.NewGuid();
        Guid identifiedOption = Guid.NewGuid();
        Guid identifiedVote = Guid.NewGuid();
        Guid identifiedTextAnswer = Guid.NewGuid();
        Guid identifiedParticipation = Guid.NewGuid();
        Guid feedbackAnalysis = Guid.NewGuid();
        DateTime createdAt = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
        DateTime completedAt = new(2026, 10, 20, 10, 1, 0, DateTimeKind.Utc);
        const string anonymousTokenHash =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string identifiedTokenHash =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string legacyText = "Legacy named feedback answer";
        const string legacyReport = "{\"summary\":\"Preserved legacy report\"}";
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
                    ({anonymousPoll}, {owner}, 'closed', 'anonymous', {createdAt}),
                    ({identifiedPoll}, {owner}, 'closed', 'identified', {createdAt});
                INSERT INTO poll_questions
                    (id, poll_id, text, position, allows_multiple_answers,
                     question_type, text_purpose)
                VALUES
                    ({anonymousQuestion}, {anonymousPoll}, 'Legacy anonymous choice', 0,
                     FALSE, 'single_choice', NULL),
                    ({identifiedChoiceQuestion}, {identifiedPoll}, 'Legacy named choice', 0,
                     FALSE, 'single_choice', NULL),
                    ({identifiedTextQuestion}, {identifiedPoll}, 'Legacy named feedback', 1,
                     FALSE, 'text', 'feedback');
                INSERT INTO poll_options (id, question_id, text, position)
                VALUES
                    ({anonymousOption}, {anonymousQuestion}, 'Anonymous option', 0),
                    ({identifiedOption}, {identifiedChoiceQuestion}, 'Named option', 0);
                INSERT INTO identified_poll_submissions (id, poll_id, respondent_name)
                VALUES ({submission}, {identifiedPoll}, 'Legacy respondent');
                INSERT INTO votes (id, option_id, created_at, identified_submission_id)
                VALUES
                    ({anonymousVote}, {anonymousOption}, {createdAt}, NULL),
                    ({identifiedVote}, {identifiedOption}, {createdAt}, {submission});
                INSERT INTO vote_participations (id, poll_id, token_hash, used_at)
                VALUES
                    ({anonymousParticipation}, {anonymousPoll}, {anonymousTokenHash}, {createdAt}),
                    ({identifiedParticipation}, {identifiedPoll}, {identifiedTokenHash}, {createdAt});
                INSERT INTO poll_text_answers
                    (id, question_id, text, identified_submission_id)
                VALUES
                    ({identifiedTextAnswer}, {identifiedTextQuestion}, {legacyText}, {submission});
                INSERT INTO poll_feedback_analyses
                    (id, poll_id, question_id, status, provider, model,
                     algorithm_version, answer_count, answer_fingerprint, report,
                     created_at, started_at, completed_at, next_attempt_at, attempts,
                     error_code, prompt_tokens, completion_tokens)
                VALUES
                    ({feedbackAnalysis}, {identifiedPoll}, {identifiedTextQuestion},
                     'completed', 'Groq', 'legacy-model', 'v1', 1,
                     'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                     CAST({legacyReport} AS jsonb), {createdAt}, {createdAt}, {completedAt},
                     NULL, 1, NULL, 12, 8);
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
            await verify.IdentifiedPollSubmissions.AsNoTracking()
                .Include(item => item.Votes)
                .Include(item => item.TextAnswers)
                .SingleAsync();
        Assert.Equal(submission, legacy.Id);
        Assert.Equal(identifiedPoll, legacy.PollId);
        Assert.Equal("Legacy respondent", legacy.RespondentName);
        Assert.Equal(identifiedVote, Assert.Single(legacy.Votes).Id);
        PollTextAnswer textAnswer = Assert.Single(legacy.TextAnswers);
        Assert.Equal(identifiedTextAnswer, textAnswer.Id);
        Assert.Equal(identifiedTextQuestion, textAnswer.QuestionId);
        Assert.Equal(legacyText, textAnswer.Text);

        Vote[] votes = await verify.Votes.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, votes.Length);
        Assert.Equal(
            new[] { anonymousVote, identifiedVote }.OrderBy(item => item),
            votes.Select(item => item.Id));
        Vote anonymousLegacyVote = Assert.Single(votes, item => item.Id == anonymousVote);
        Assert.Equal(anonymousOption, anonymousLegacyVote.OptionId);
        Assert.Null(anonymousLegacyVote.IdentifiedSubmissionId);

        VoteParticipation[] participations = await verify.VoteParticipations.AsNoTracking()
            .OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, participations.Length);
        Assert.Contains(participations, item => item.Id == anonymousParticipation &&
            item.PollId == anonymousPoll && item.TokenHash == anonymousTokenHash);
        Assert.Contains(participations, item => item.Id == identifiedParticipation &&
            item.PollId == identifiedPoll && item.TokenHash == identifiedTokenHash);

        PollFeedbackAnalysis analysis = await verify.PollFeedbackAnalyses.AsNoTracking()
            .SingleAsync();
        Assert.Equal(feedbackAnalysis, analysis.Id);
        Assert.Equal(identifiedPoll, analysis.PollId);
        Assert.Equal(identifiedTextQuestion, analysis.QuestionId);
        Assert.Equal(FeedbackAnalysisStatus.Completed, analysis.Status);
        Assert.Equal("Groq", analysis.Provider);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(analysis.ReportJson));
        Assert.Equal("Preserved legacy report",
            report.RootElement.GetProperty("summary").GetString());
        Assert.Equal(completedAt, analysis.CompletedAt);
        Assert.Equal(1, analysis.AnswerCount);
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
