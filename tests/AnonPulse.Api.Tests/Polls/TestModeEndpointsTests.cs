using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Auth;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Tests.Auth;
using AnonPulse.Api.Tests.Voting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class TestModeEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_test_mode_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();
    private PublicVotingWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new PublicVotingWebApplicationFactory(_postgres.GetConnectionString());
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
    public async Task SurveyDefaultsRemainCompatible_AndTestConfigurationIsValidated()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage surveyResponse = await client.PostAsJsonAsync(
            "/api/polls",
            new { Question = "Survey?", Options = new[] { "Yes", "No" } });
        PollResponse survey = (await surveyResponse.Content.ReadFromJsonAsync<PollResponse>())!;
        HttpResponseMessage identifiedSurveyResponse = await client.PostAsJsonAsync(
            "/api/polls",
            new
            {
                ResponseMode = "identified",
                Question = "Named survey?",
                Options = new[] { "Yes", "No" }
            });
        PollResponse identifiedSurvey =
            (await identifiedSurveyResponse.Content.ReadFromJsonAsync<PollResponse>())!;
        HttpResponseMessage anonymousTest = await client.PostAsJsonAsync(
            "/api/polls", TestRequest(5, responseMode: "anonymous"));
        HttpResponseMessage invalidScale = await client.PostAsJsonAsync(
            "/api/polls", TestRequest(20));

        Assert.Equal(HttpStatusCode.Created, surveyResponse.StatusCode);
        Assert.Equal("survey", survey.Kind);
        Assert.Equal("anonymous", survey.ResponseMode);
        Assert.Equal(HttpStatusCode.Created, identifiedSurveyResponse.StatusCode);
        Assert.Equal("survey", identifiedSurvey.Kind);
        Assert.Equal("identified", identifiedSurvey.ResponseMode);
        Assert.Equal(HttpStatusCode.BadRequest, anonymousTest.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidScale.StatusCode);

        foreach (int scale in new[] { 5, 10, 12, 100 })
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/polls", ChoiceOnlyTestRequest(scale));
            PollResponse test = (await response.Content.ReadFromJsonAsync<PollResponse>())!;
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("test", test.Kind);
            Assert.Equal("identified", test.ResponseMode);
            Assert.Equal(scale, test.TestScale);
            Assert.Single(test.Questions[0].CorrectOptionIds!);
            Assert.Equal(2m, test.Questions[0].Weight);
        }
    }

    public static TheoryData<decimal, HttpStatusCode> WeightCases => new()
    {
        { 0m, HttpStatusCode.BadRequest },
        { 0.001m, HttpStatusCode.BadRequest },
        { 0.01m, HttpStatusCode.Created },
        { 1.234m, HttpStatusCode.BadRequest },
        { 10000m, HttpStatusCode.Created },
        { 10000.01m, HttpStatusCode.BadRequest }
    };

    [Theory]
    [MemberData(nameof(WeightCases))]
    public async Task TestWeight_MatchesNumericTenTwoValidation(
        decimal weight,
        HttpStatusCode expectedStatus)
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/polls",
            new
            {
                Title = "Weight validation",
                Kind = "test",
                ResponseMode = "identified",
                TestScale = 5,
                Questions = new[]
                {
                    new
                    {
                        Text = "Question",
                        Type = "single_choice",
                        Options = new[] { "A", "B" },
                        Weight = weight,
                        CorrectOptionIndexes = new[] { 0 }
                    }
                }
            });

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.BadRequest)
        {
            Assert.Equal("application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task PublicSubmission_IsSeparateAtomicIdempotentAndKeepsPrivateRubricPrivate()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser stranger = await CreateUserAsync(RoleNames.Teacher);
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient strangerClient = _factory.CreateHttpsClient();
        using HttpClient adminClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(strangerClient, stranger);
        await LoginAsync(adminClient, admin);
        PollResponse test = await CreateAsync(ownerClient, TestRequest(10));
        await PublishAsync(ownerClient, test.Id);
        PollResponse sessionSurvey = await CreateAsync(ownerClient, new
        {
            Question = "Survey session",
            Options = new[] { "Yes", "No" }
        });
        await PublishAsync(ownerClient, sessionSurvey.Id);

        using HttpClient publicClient = _factory.CreateHttpsClient();
        HttpResponseMessage publicResponse =
            await publicClient.GetAsync($"/api/public/polls/{test.Id}");
        string publicJson = await publicResponse.Content.ReadAsStringAsync();
        PublicPollResponse publicPoll = (await publicResponse.Content
            .ReadFromJsonAsync<PublicPollResponse>())!;
        Assert.Equal("test", publicPoll.Kind);
        Assert.False(publicResponse.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("correctOption", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedAnswer", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gradingCriteria", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("weight", publicJson, StringComparison.OrdinalIgnoreCase);
        HttpResponseMessage surveyGet =
            await publicClient.GetAsync($"/api/public/polls/{sessionSurvey.Id}");
        Assert.True(surveyGet.Headers.Contains("Set-Cookie"));

        Guid requestId = Guid.NewGuid();
        object validBody = SubmissionBody(publicPoll, requestId, includeExtraMultiple: false);
        HttpResponseMessage first = await SubmitAsync(publicClient, test.Id, validBody);
        TestSubmissionCreatedResponse created =
            (await first.Content.ReadFromJsonAsync<TestSubmissionCreatedResponse>())!;
        HttpResponseMessage replay = await SubmitAsync(publicClient, test.Id, validBody);
        HttpResponseMessage changed = await SubmitAsync(
            publicClient, test.Id,
            SubmissionBody(publicPoll, requestId, includeExtraMultiple: true));
        HttpResponseMessage surveyVotes = await SubmitSurveyVoteAsync(
            publicClient, test.Id, publicPoll);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.NotEqual(Guid.Empty, created.AttemptId);
        Assert.True(created.AccessSecret.Length >= 40);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Contains("secret_unavailable", await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("idempotency_key_reused", await changed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, surveyVotes.StatusCode);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            NamedAttempt attempt = await db.NamedAttempts
                .Include(item => item.Answers)
                .SingleAsync(item => item.Id == created.AttemptId);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(created.AccessSecret)));
            Assert.Equal(expectedHash, attempt.AccessSecretHash);
            Assert.DoesNotContain(created.AccessSecret, attempt.AccessSecretHash, StringComparison.Ordinal);
            Assert.Equal("Иванов Иван", attempt.RespondentName);
            Assert.Equal("ИВТ-101", attempt.RespondentGroup);
            Assert.False(attempt.IsFullyGraded);
            Assert.False(attempt.IsFinal);
            Assert.Null(attempt.NormalizedScore);
            Assert.Equal(5m, attempt.RawScore);
            Assert.Equal(10m, attempt.MaximumScore);
            Assert.Equal(2, attempt.Answers.Count(item => item.GradingState == "auto_graded"));
            Assert.Single(attempt.Answers, item => item.GradingState == "pending" && item.Score is null);
            Assert.Empty(db.VoteParticipations);
            Assert.Empty(db.Votes);
            Assert.Empty(db.PollTextAnswers);
            Assert.Empty(db.IdentifiedPollSubmissions);
        }

        Assert.Equal(HttpStatusCode.Conflict,
            (await ownerClient.GetAsync($"/api/polls/{test.Id}/results")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await ownerClient.GetAsync($"/api/polls/{test.Id}/test-attempts")).StatusCode);
        await CloseAsync(ownerClient, test.Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await strangerClient.GetAsync($"/api/polls/{test.Id}/test-attempts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await ownerClient.GetAsync($"/api/polls/{test.Id}/test-attempts")).StatusCode);
        HttpResponseMessage adminAttempts =
            await adminClient.GetAsync($"/api/polls/{test.Id}/test-attempts?page=1&pageSize=10");
        PagedResponse<TestAttemptResponse> page =
            (await adminAttempts.Content.ReadFromJsonAsync<PagedResponse<TestAttemptResponse>>())!;
        Assert.Equal(HttpStatusCode.OK, adminAttempts.StatusCode);
        Assert.Single(page.Items);
        Assert.DoesNotContain(created.AccessSecret, await adminAttempts.Content.ReadAsStringAsync());

        HttpResponseMessage immutable = await ownerClient.PutAsJsonAsync(
            $"/api/polls/{test.Id}", TestRequest(100));
        Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);

        string csrf = (await adminClient.GetFromJsonAsync<CsrfTokenResponse>(
            "/api/auth/csrf-token"))!.Token;
        using var delete = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/admin/polls/{test.Id}?confirm=true");
        delete.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent,
            (await adminClient.SendAsync(delete)).StatusCode);
        using IServiceScope deletedScope = _factory.Services.CreateScope();
        ApplicationDbContext deletedDb = deletedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        Assert.False(await deletedDb.NamedAttempts.AnyAsync(
            item => item.PollId == test.Id));
        Assert.False(await deletedDb.PollQuestionCorrectOptions.AnyAsync(
            item => item.Question.PollId == test.Id));
    }

    [Fact]
    public async Task ChoiceScoring_AllScalesExactSetAtomicityAndConcurrentRetry()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient teacherClient = _factory.CreateHttpsClient();
        await LoginAsync(teacherClient, teacher);
        foreach (int scale in new[] { 5, 10, 12, 100 })
        {
            PollResponse test = await CreateAsync(teacherClient, ChoiceOnlyTestRequest(scale));
            await PublishAsync(teacherClient, test.Id);
            using HttpClient participant = _factory.CreateHttpsClient();
            PublicPollResponse poll = (await participant.GetFromJsonAsync<PublicPollResponse>(
                $"/api/public/polls/{test.Id}"))!;
            Guid requestId = Guid.NewGuid();
            var body = new
            {
                RespondentName = " Student ",
                RespondentGroup = " Group ",
                ClientRequestId = requestId,
                Answers = new object[]
                {
                    new { QuestionId = poll.Questions[0].Id, OptionId = poll.Questions[0].Options[0].Id },
                    new { QuestionId = poll.Questions[1].Id, OptionId = poll.Questions[1].Options[0].Id },
                    new { QuestionId = poll.Questions[1].Id, OptionId = poll.Questions[1].Options[2].Id }
                }
            };
            using HttpClient retryClient = _factory.CreateHttpsClient();
            HttpResponseMessage[] responses = await Task.WhenAll(
                SubmitAsync(participant, test.Id, body),
                SubmitAsync(retryClient, test.Id, body));
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict);

            using IServiceScope scope = _factory.Services.CreateScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            NamedAttempt attempt = await db.NamedAttempts.AsNoTracking()
                .SingleAsync(item => item.PollId == test.Id &&
                                     item.ClientRequestId == requestId);
            Assert.True(attempt.IsFullyGraded);
            Assert.False(attempt.IsFinal);
            Assert.Equal(5m, attempt.RawScore);
            Assert.Equal(scale, attempt.NormalizedScore);

            if (scale == 5)
            {
                HttpResponseMessage extra = await SubmitAsync(
                    participant,
                    test.Id,
                    ChoiceSubmissionBody(poll, Guid.NewGuid(), [0, 1, 2]));
                HttpResponseMessage partial = await SubmitAsync(
                    participant,
                    test.Id,
                    ChoiceSubmissionBody(poll, Guid.NewGuid(), [0]));
                Assert.Equal(HttpStatusCode.Created, extra.StatusCode);
                Assert.Equal(HttpStatusCode.Created, partial.StatusCode);
                db.ChangeTracker.Clear();
                decimal[] otherScores = await db.NamedAttempts.AsNoTracking()
                    .Where(item => item.PollId == test.Id &&
                                   item.ClientRequestId != requestId)
                    .OrderBy(item => item.CreatedAt)
                    .Select(item => item.RawScore)
                    .ToArrayAsync();
                Assert.Equal(new[] { 2m, 2m }, otherScores);
            }
        }

        PollResponse invalidTest = await CreateAsync(teacherClient, TestRequest(5));
        await PublishAsync(teacherClient, invalidTest.Id);
        using HttpClient invalidClient = _factory.CreateHttpsClient();
        PublicPollResponse invalidPoll = (await invalidClient.GetFromJsonAsync<PublicPollResponse>(
            $"/api/public/polls/{invalidTest.Id}"))!;
        var incomplete = new
        {
            RespondentName = "Name",
            RespondentGroup = "Group",
            ClientRequestId = Guid.NewGuid(),
            Answers = new[]
            {
                new { QuestionId = invalidPoll.Questions[0].Id,
                    OptionId = invalidPoll.Questions[0].Options[0].Id }
            }
        };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SubmitAsync(invalidClient, invalidTest.Id, incomplete)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SubmitAsync(
            invalidClient,
            invalidTest.Id,
            IdentityValidationBody(invalidPoll, " ", "Group"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SubmitAsync(
            invalidClient,
            invalidTest.Id,
            IdentityValidationBody(invalidPoll, "Name", "\t"))).StatusCode);
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        Assert.False(await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .NamedAttempts.AnyAsync(item => item.PollId == invalidTest.Id));
    }

    private static object TestRequest(int scale, string responseMode = "identified") => new
    {
        Title = "Knowledge test",
        Kind = "test",
        ResponseMode = responseMode,
        TestScale = scale,
        Questions = new object[]
        {
            new { Text = "Single", Type = "single_choice", Options = new[] { "A", "B" }, Weight = 2, CorrectOptionIndexes = new[] { 0 } },
            new { Text = "Multiple", Type = "multiple_choice", Options = new[] { "C", "D", "E" }, Weight = 3, CorrectOptionIndexes = new[] { 0, 2 } },
            new
            {
                Text = "Explain",
                Type = "text",
                TextPurpose = "knowledge",
                Options = Array.Empty<string>(),
                KnowledgeSettings = new { ExpectedAnswer = "Expected private", GradingCriteria = "Criteria private", MaxScore = 5 }
            }
        }
    };

    private static object ChoiceOnlyTestRequest(int scale) => new
    {
        Title = "Choice test",
        Kind = "test",
        ResponseMode = "identified",
        TestScale = scale,
        Questions = new object[]
        {
            new { Text = "Single", Type = "single_choice", Options = new[] { "A", "B" }, Weight = 2, CorrectOptionIndexes = new[] { 0 } },
            new { Text = "Multiple", Type = "multiple_choice", Options = new[] { "C", "D", "E" }, Weight = 3, CorrectOptionIndexes = new[] { 0, 2 } }
        }
    };

    private static object SubmissionBody(
        PublicPollResponse poll,
        Guid requestId,
        bool includeExtraMultiple)
    {
        var answers = new List<object>
        {
            new { QuestionId = poll.Questions[0].Id, OptionId = poll.Questions[0].Options[0].Id },
            new { QuestionId = poll.Questions[1].Id, OptionId = poll.Questions[1].Options[0].Id },
            new { QuestionId = poll.Questions[1].Id, OptionId = poll.Questions[1].Options[2].Id },
            new { QuestionId = poll.Questions[2].Id, Text = "  Text answer  " }
        };
        if (includeExtraMultiple)
        {
            answers.Insert(3, new
            {
                QuestionId = poll.Questions[1].Id,
                OptionId = poll.Questions[1].Options[1].Id
            });
        }
        return new
        {
            RespondentName = "  Иванов   Иван ",
            RespondentGroup = " ИВТ-101 ",
            ClientRequestId = requestId,
            Answers = answers
        };
    }

    private static object ChoiceSubmissionBody(
        PublicPollResponse poll,
        Guid requestId,
        IReadOnlyList<int> multipleIndexes)
    {
        var answers = new List<object>
        {
            new
            {
                QuestionId = poll.Questions[0].Id,
                OptionId = poll.Questions[0].Options[0].Id
            }
        };
        answers.AddRange(multipleIndexes.Select(index => (object)new
        {
            QuestionId = poll.Questions[1].Id,
            OptionId = poll.Questions[1].Options[index].Id
        }));
        return new
        {
            RespondentName = "Set test",
            RespondentGroup = "Group",
            ClientRequestId = requestId,
            Answers = answers
        };
    }

    private static object IdentityValidationBody(
        PublicPollResponse poll,
        string respondentName,
        string respondentGroup) => new
        {
            RespondentName = respondentName,
            RespondentGroup = respondentGroup,
            ClientRequestId = Guid.NewGuid(),
            Answers = new object[]
            {
                new { QuestionId = poll.Questions[0].Id, OptionId = poll.Questions[0].Options[0].Id },
                new { QuestionId = poll.Questions[1].Id, OptionId = poll.Questions[1].Options[0].Id },
                new { QuestionId = poll.Questions[1].Id, OptionId = poll.Questions[1].Options[2].Id },
                new { QuestionId = poll.Questions[2].Id, Text = "Text" }
            }
        };

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client, Guid pollId, object body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/public/polls/{pollId}/test-submissions")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-AnonPulse-Test", "1");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SubmitSurveyVoteAsync(
        HttpClient client, Guid pollId, PublicPollResponse poll)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/public/polls/{pollId}/votes")
        {
            Content = JsonContent.Create(new
            {
                RespondentName = "Name",
                Answers = new[]
                {
                    new { QuestionId = poll.Questions[0].Id,
                        OptionId = poll.Questions[0].Options[0].Id }
                }
            })
        };
        request.Headers.Add("X-AnonPulse-Vote", "1");
        return await client.SendAsync(request);
    }

    private async Task<SeededUser> CreateUserAsync(string role)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roles = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roles.RoleExistsAsync(role))
        {
            Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);
        }
        string email = $"test-mode-{Guid.NewGuid():N}@example.test";
        const string password = "Test-mode-password-123!";
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

    private static async Task LoginAsync(HttpClient client, SeededUser user) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/auth/login", new { user.Email, user.Password })).StatusCode);

    private static async Task<PollResponse> CreateAsync(HttpClient client, object body)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/polls", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PollResponse>())!;
    }

    private static async Task PublishAsync(HttpClient client, Guid id) =>
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/polls/{id}/publish", null)).StatusCode);

    private static async Task CloseAsync(HttpClient client, Guid id) =>
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/polls/{id}/close", null)).StatusCode);
}
