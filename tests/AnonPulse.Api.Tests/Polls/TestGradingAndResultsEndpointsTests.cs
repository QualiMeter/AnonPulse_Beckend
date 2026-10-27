using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AnonPulse.Api.Contracts.Auth;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Tests.Auth;
using AnonPulse.Api.Tests.Voting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class TestGradingAndResultsEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_test_grading_results")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();
    private PublicVotingWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new PublicVotingWebApplicationFactory(
            _postgres.GetConnectionString(),
            new Dictionary<string, string?>
            {
                ["TestResults:RequestsPerClient"] = "200"
            });
        using IServiceScope scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ManualGrading_Finalization_AuditAndPrivateResultAreSafe()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededTest seeded = await SeedTestAsync(owner.Id, PollStatus.Open);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        string csrf = await GetCsrfAsync(ownerClient);

        HttpResponseMessage beforeClose = await GradeAsync(ownerClient, seeded,
            seeded.PendingAnswerId, new { score = 2.5m }, csrf);
        AssertError(beforeClose, HttpStatusCode.Conflict, "test_not_closed");
        await SetStatusAsync(seeded.PollId, PollStatus.Closed);

        PagedResponse<TestAttemptResponse> attempts = (await ownerClient.GetFromJsonAsync<
            PagedResponse<TestAttemptResponse>>(
            $"/api/polls/{seeded.PollId}/test-attempts"))!;
        Assert.Contains(attempts.Items.Single().Answers,
            answer => answer.AnswerId == seeded.PendingAnswerId);

        HttpResponseMessage pendingFinalize = await FinalizeAsync(ownerClient, seeded, csrf);
        AssertError(pendingFinalize, HttpStatusCode.Conflict,
            "attempt_has_pending_answers");
        foreach ((decimal score, string code) in new[]
                 {
                     (-1m, "score_out_of_range"),
                     (3.01m, "score_out_of_range"),
                     (1.234m, "score_precision_invalid")
                 })
        {
            HttpResponseMessage invalid = await GradeAsync(ownerClient, seeded,
                seeded.PendingAnswerId, new { score }, csrf);
            AssertError(invalid, HttpStatusCode.BadRequest, code);
        }

        HttpResponseMessage knowledgeGrade = await GradeAsync(ownerClient, seeded,
            seeded.PendingAnswerId, new { score = 2.5m }, csrf);
        GradeTestAnswerResponse knowledge = (await knowledgeGrade.Content
            .ReadFromJsonAsync<GradeTestAnswerResponse>())!;
        Assert.Equal(HttpStatusCode.OK, knowledgeGrade.StatusCode);
        Assert.Equal(seeded.PendingAnswerId, knowledge.AnswerId);
        Assert.Equal(2.5m, knowledge.Score);
        Assert.Equal(TestAnswerGradingStates.TeacherGraded, knowledge.GradingState);

        HttpResponseMessage corrected = await GradeAsync(ownerClient, seeded,
            seeded.AutoAnswerId, new { score = 1m }, csrf);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        using HttpClient participant = _factory.CreateHttpsClient();
        HttpResponseMessage notFinal = await ResultAsync(
            participant, seeded, seeded.AccessSecret);
        string notFinalJson = await notFinal.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, notFinal.StatusCode);
        Assert.True(notFinal.Headers.CacheControl?.NoStore);
        Assert.Contains("\"isFinal\":false", notFinalJson);
        Assert.DoesNotContain("respondentName", notFinalJson);
        Assert.DoesNotContain("rawScore", notFinalJson);
        Assert.DoesNotContain("answers", notFinalJson);

        HttpResponseMessage finalizedResponse = await FinalizeAsync(ownerClient, seeded, csrf);
        FinalizeTestAttemptResponse finalized = (await finalizedResponse.Content
            .ReadFromJsonAsync<FinalizeTestAttemptResponse>())!;
        Assert.Equal(HttpStatusCode.OK, finalizedResponse.StatusCode);
        Assert.Equal(3.5m, finalized.RawScore);
        Assert.Equal(5m, finalized.MaximumScore);
        Assert.Equal(7m, finalized.NormalizedScore);
        Assert.True(finalized.IsFullyGraded);
        Assert.True(finalized.IsFinal);
        Assert.Equal(owner.Id, finalized.FinalizedByUserId);

        FinalizeTestAttemptResponse replay = (await (await FinalizeAsync(
            ownerClient, seeded, csrf)).Content
            .ReadFromJsonAsync<FinalizeTestAttemptResponse>())!;
        Assert.Equal(finalized.FinalizedAt, replay.FinalizedAt);
        Assert.Equal(finalized.FinalizedByUserId, replay.FinalizedByUserId);

        HttpResponseMessage immutable = await GradeAsync(ownerClient, seeded,
            seeded.PendingAnswerId, new { score = 3m }, csrf);
        AssertError(immutable, HttpStatusCode.Conflict, "attempt_finalized");

        TestAttemptGradeHistoryResponse history = (await ownerClient.GetFromJsonAsync<
            TestAttemptGradeHistoryResponse>(
            $"/api/polls/{seeded.PollId}/test-attempts/{seeded.AttemptId}/grade-history"))!;
        Assert.Equal(2, history.Changes.Count);
        TestGradeChangeResponse autoCorrection = Assert.Single(history.Changes,
            change => change.AnswerId == seeded.AutoAnswerId);
        Assert.Equal(2m, autoCorrection.OldScore);
        Assert.Equal(1m, autoCorrection.NewScore);
        Assert.Equal("manual", autoCorrection.Source);
        Assert.Equal(owner.Id, history.FinalizedByUserId);

        HttpResponseMessage wrong = await ResultAsync(participant, seeded,
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
        HttpResponseMessage wrongAttempt = await participant.PostAsJsonAsync(
            $"/api/public/polls/{seeded.PollId}/test-attempts/{Guid.NewGuid()}/result",
            new { accessSecret = seeded.AccessSecret });
        AssertError(wrong, HttpStatusCode.NotFound, "test_result_not_found");
        AssertError(wrongAttempt, HttpStatusCode.NotFound, "test_result_not_found");

        HttpResponseMessage resultResponse = await ResultAsync(
            participant, seeded, seeded.AccessSecret);
        string resultJson = await resultResponse.Content.ReadAsStringAsync();
        PublicTestResultResponse result = (await resultResponse.Content
            .ReadFromJsonAsync<PublicTestResultResponse>())!;
        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.True(resultResponse.Headers.CacheControl?.NoStore);
        Assert.Equal("Final test", result.TestTitle);
        Assert.Equal("Student Name", result.RespondentName);
        Assert.Equal("G-1", result.RespondentGroup);
        Assert.Equal(3.5m, result.RawScore);
        Assert.Equal(7m, result.NormalizedScore);
        Assert.Equal(2, result.Answers!.Count);
        Assert.DoesNotContain(seeded.AccessSecret, resultJson);
        Assert.DoesNotContain("accessSecretHash", resultJson);
        Assert.DoesNotContain("expectedAnswer", resultJson);
        Assert.DoesNotContain("gradingCriteria", resultJson);
        Assert.DoesNotContain("gradeHistory", resultJson);
    }

    [Fact]
    public async Task AiSuggestionRequiresExplicitAction_CanBeOverridden_AndNeedsReviewDoesNotBlock()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededTest seeded = await SeedTestAsync(owner.Id, PollStatus.Closed);
        Guid suggestionId = await SeedSuggestionAsync(seeded, needsReview: true);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, owner);
        string csrf = await GetCsrfAsync(client);

        HttpResponseMessage accepted = await GradeAsync(client, seeded,
            seeded.PendingAnswerId, new { suggestionId }, csrf);
        GradeTestAnswerResponse grade = (await accepted.Content
            .ReadFromJsonAsync<GradeTestAnswerResponse>())!;
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2m, grade.Score);

        HttpResponseMessage changed = await GradeAsync(client, seeded,
            seeded.PendingAnswerId, new { score = 2.75m }, csrf);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        TestAttemptGradeHistoryResponse history = (await client.GetFromJsonAsync<
            TestAttemptGradeHistoryResponse>(
            $"/api/polls/{seeded.PollId}/test-attempts/{seeded.AttemptId}/grade-history"))!;
        Assert.Collection(history.Changes,
            first =>
            {
                Assert.Equal("ai_suggestion", first.Source);
                Assert.Equal(suggestionId, first.SuggestionId);
                Assert.Null(first.OldScore);
                Assert.Equal(2m, first.NewScore);
            },
            second =>
            {
                Assert.Equal("manual", second.Source);
                Assert.Null(second.SuggestionId);
                Assert.Equal(2m, second.OldScore);
                Assert.Equal(2.75m, second.NewScore);
            });

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.PollKnowledgeGradingSuggestions.AnyAsync(
            item => item.Id == suggestionId && item.NeedsReview));
    }

    [Fact]
    public async Task OwnershipAndNestedIdentifiersAreEnforced_AndSurveyIsRejected()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser stranger = await CreateUserAsync(RoleNames.Teacher);
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededTest first = await SeedTestAsync(owner.Id, PollStatus.Closed);
        SeededTest second = await SeedTestAsync(owner.Id, PollStatus.Closed);
        SeededTest survey = await SeedTestAsync(owner.Id, PollStatus.Closed, PollKind.Survey);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient strangerClient = _factory.CreateHttpsClient();
        using HttpClient adminClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(strangerClient, stranger);
        await LoginAsync(adminClient, admin);
        string ownerCsrf = await GetCsrfAsync(ownerClient);
        string strangerCsrf = await GetCsrfAsync(strangerClient);
        string adminCsrf = await GetCsrfAsync(adminClient);

        Assert.Equal(HttpStatusCode.NotFound,
            (await GradeAsync(strangerClient, first, first.PendingAnswerId,
                new { score = 1m }, strangerCsrf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await strangerClient.GetAsync(
                $"/api/polls/{first.PollId}/test-attempts/{first.AttemptId}/grade-history"))
            .StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await GradeAsync(ownerClient, first, second.PendingAnswerId,
                new { score = 1m }, ownerCsrf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await GradeAsync(ownerClient,
                first with { PollId = second.PollId }, first.PendingAnswerId,
                new { score = 1m }, ownerCsrf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await GradeAsync(adminClient, first, first.PendingAnswerId,
                new { score = 1m }, adminCsrf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await adminClient.GetAsync(
                $"/api/polls/{first.PollId}/test-attempts/{first.AttemptId}/grade-history"))
            .StatusCode);
        HttpResponseMessage surveyGrade = await GradeAsync(ownerClient, survey,
            survey.PendingAnswerId, new { score = 1m }, ownerCsrf);
        AssertError(surveyGrade, HttpStatusCode.Conflict, "not_a_test");
    }

    [Fact]
    public async Task ConcurrentGradeAndFinalizeNeverProducePartialFinalState()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededTest seeded = await SeedTestAsync(owner.Id, PollStatus.Closed);
        using HttpClient gradeClient = _factory.CreateHttpsClient();
        using HttpClient finalizeClient = _factory.CreateHttpsClient();
        await LoginAsync(gradeClient, owner);
        await LoginAsync(finalizeClient, owner);
        string gradeCsrf = await GetCsrfAsync(gradeClient);
        string finalizeCsrf = await GetCsrfAsync(finalizeClient);

        Task<HttpResponseMessage> grade = GradeAsync(gradeClient, seeded,
            seeded.PendingAnswerId, new { score = 2m }, gradeCsrf);
        Task<HttpResponseMessage> finalize = FinalizeAsync(
            finalizeClient, seeded, finalizeCsrf);
        HttpResponseMessage[] responses = await Task.WhenAll(grade, finalize);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        NamedAttempt attempt = await db.NamedAttempts.AsNoTracking()
            .Include(item => item.Answers)
            .SingleAsync(item => item.Id == seeded.AttemptId);
        Assert.All(attempt.Answers, answer => Assert.NotNull(answer.Score));
        if (attempt.IsFinal)
        {
            Assert.True(attempt.IsFullyGraded);
            Assert.NotNull(attempt.NormalizedScore);
            Assert.NotNull(attempt.FinalizedAt);
            Assert.NotNull(attempt.FinalizedByUserId);
        }
        else
        {
            Assert.Null(attempt.FinalizedAt);
            Assert.Null(attempt.FinalizedByUserId);
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        }
    }

    private async Task<SeededTest> SeedTestAsync(Guid ownerId, PollStatus status,
        PollKind kind = PollKind.Test)
    {
        string secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        Guid pollId = Guid.NewGuid();
        Guid knowledgeQuestionId = Guid.NewGuid();
        Guid choiceQuestionId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid pendingAnswerId = Guid.NewGuid();
        Guid autoAnswerId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var poll = new Poll
        {
            Id = pollId,
            CreatorId = ownerId,
            Title = kind == PollKind.Test ? "Final test" : "Survey",
            Kind = kind,
            ResponseMode = kind == PollKind.Test
                ? PollResponseMode.Identified
                : PollResponseMode.Anonymous,
            TestScale = kind == PollKind.Test ? 10 : null,
            Status = PollStatus.Open,
            CreatedAt = now,
            PublishedAt = now,
            ClosedAt = null
        };
        var knowledgeQuestion = new PollQuestion
        {
            Id = knowledgeQuestionId,
            PollId = pollId,
            Poll = poll,
            Text = "Explain",
            Position = 0,
            Type = PollQuestionType.Text,
            TextPurpose = kind == PollKind.Test
                ? TextQuestionPurpose.Knowledge
                : TextQuestionPurpose.Feedback
        };
        var choiceQuestion = new PollQuestion
        {
            Id = choiceQuestionId,
            PollId = pollId,
            Poll = poll,
            Text = "Choose",
            Position = 1,
            Type = PollQuestionType.SingleChoice,
            TestWeight = kind == PollKind.Test ? 2m : null
        };
        poll.Questions.Add(knowledgeQuestion);
        poll.Questions.Add(choiceQuestion);
        if (kind == PollKind.Test)
        {
            knowledgeQuestion.KnowledgeSettings = new PollQuestionKnowledgeSettings
            {
                QuestionId = knowledgeQuestionId,
                Question = knowledgeQuestion,
                ExpectedAnswer = "Private expected answer",
                GradingCriteria = "Private rubric",
                MaxScore = 3
            };
        }
        if (kind == PollKind.Survey)
        {
            db.Add(poll);
            await db.SaveChangesAsync();
            poll.Status = status;
            poll.ClosedAt = status == PollStatus.Closed ? now : null;
            await db.SaveChangesAsync();
            return new SeededTest(pollId, attemptId, pendingAnswerId, autoAnswerId, secret);
        }
        var attempt = new NamedAttempt
        {
            Id = attemptId,
            PollId = pollId,
            Poll = poll,
            RespondentName = "Student Name",
            RespondentGroup = "G-1",
            ClientRequestId = Guid.NewGuid(),
            RequestHash = new string('a', 64),
            AccessSecretHash = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(secret))),
            CreatedAt = now,
            CompletedAt = now,
            RawScore = 2m,
            MaximumScore = 5m,
            IsFullyGraded = false,
            IsFinal = false
        };
        attempt.Answers.Add(new NamedAttemptAnswer
        {
            Id = pendingAnswerId,
            AttemptId = attemptId,
            Attempt = attempt,
            QuestionId = knowledgeQuestionId,
            Question = knowledgeQuestion,
            Text = "My own answer",
            GradingState = TestAnswerGradingStates.Pending,
            Score = null,
            MaximumScore = 3m
        });
        attempt.Answers.Add(new NamedAttemptAnswer
        {
            Id = autoAnswerId,
            AttemptId = attemptId,
            Attempt = attempt,
            QuestionId = choiceQuestionId,
            Question = choiceQuestion,
            GradingState = TestAnswerGradingStates.AutoGraded,
            Score = 2m,
            MaximumScore = 2m
        });
        db.AddRange(poll, attempt);
        await db.SaveChangesAsync();
        poll.Status = status;
        poll.ClosedAt = status == PollStatus.Closed ? now : null;
        await db.SaveChangesAsync();
        return new SeededTest(pollId, attemptId, pendingAnswerId, autoAnswerId, secret);
    }

    private async Task<Guid> SeedSuggestionAsync(SeededTest seeded, bool needsReview)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        NamedAttemptAnswer answer = await db.NamedAttemptAnswers.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.PendingAnswerId);
        var job = new PollKnowledgeGradingJob
        {
            Id = Guid.NewGuid(),
            PollId = seeded.PollId,
            QuestionId = answer.QuestionId,
            Status = KnowledgeGradingJobStatus.Completed,
            Provider = "fake",
            Model = "fake-model",
            AlgorithmVersion = "test-v1",
            SnapshotFingerprint = new string('f', 64),
            ExpectedAnswerSnapshot = "private",
            GradingCriteriaSnapshot = "private",
            MaxScoreSnapshot = 3,
            AnswersSnapshotJson = "[]",
            AnswerCount = 1,
            SuggestedCount = 1,
            ReservedInputTokens = 1,
            ReservedOutputTokens = 1,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        var suggestion = new PollKnowledgeGradingSuggestion
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AnswerId = answer.Id,
            SuggestedScore = 2,
            MaxScore = 3,
            Explanation = "Fake explanation",
            CorrectPointsJson = "[]",
            MissingOrIncorrectPointsJson = "[]",
            NeedsReview = needsReview,
            ReviewReasonsJson = needsReview ? "[\"manual review\"]" : "[]",
            CreatedAt = DateTime.UtcNow
        };
        db.AddRange(job, suggestion);
        await db.SaveChangesAsync();
        return suggestion.Id;
    }

    private async Task<SeededUser> CreateUserAsync(string role)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roles = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roles.RoleExistsAsync(role))
            Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);
        string email = $"grading-{Guid.NewGuid():N}@example.test";
        const string password = "Grading-password-123!";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return new SeededUser(user.Id, email, password);
    }

    private async Task SetStatusAsync(Guid pollId, PollStatus status)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Poll poll = await db.Polls.SingleAsync(item => item.Id == pollId);
        poll.Status = status;
        poll.ClosedAt = status == PollStatus.Closed ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/auth/login", new { user.Email, user.Password })).StatusCode);

    private static async Task<string> GetCsrfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/auth/csrf-token"))!.Token;

    private static async Task<HttpResponseMessage> GradeAsync(HttpClient client,
        SeededTest seeded, Guid answerId, object body, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/polls/{seeded.PollId}/test-attempts/{seeded.AttemptId}/answers/{answerId}/grade")
        { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> FinalizeAsync(HttpClient client,
        SeededTest seeded, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/polls/{seeded.PollId}/test-attempts/{seeded.AttemptId}/finalize");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ResultAsync(HttpClient client,
        SeededTest seeded, string secret) => client.PostAsJsonAsync(
        $"/api/public/polls/{seeded.PollId}/test-attempts/{seeded.AttemptId}/result",
        new { accessSecret = secret });

    private static void AssertError(HttpResponseMessage response,
        HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Contains(code, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    private sealed record SeededTest(Guid PollId, Guid AttemptId,
        Guid PendingAnswerId, Guid AutoAnswerId, string AccessSecret);
}
