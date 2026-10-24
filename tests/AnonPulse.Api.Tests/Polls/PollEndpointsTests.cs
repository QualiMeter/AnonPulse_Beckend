using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Tests.Auth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class PollEndpointsTests
{
    [Fact]
    public async Task Create_WithValidRequest_PersistsDraftForCurrentTeacher()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/polls",
            new
            {
                CreatorId = Guid.NewGuid(),
                Question = "  Какой   формат занятий удобнее? ",
                Options = new[] { "Очный", " Дистанционный ", "Гибридный" }
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        PollResponse? poll = await response.Content.ReadFromJsonAsync<PollResponse>();
        Assert.NotNull(poll);
        Assert.Equal($"/api/polls/{poll.Id}", response.Headers.Location?.AbsolutePath);
        Assert.Equal("Какой формат занятий удобнее?", poll.Question);
        Assert.Equal("draft", poll.Status);
        Assert.Equal(DateTimeKind.Utc, poll.CreatedAt.Kind);
        Assert.Null(poll.PublishedAt);
        Assert.Null(poll.ClosedAt);
        PollQuestionResponse legacyQuestion = Assert.Single(poll.Questions);
        Assert.Equal(poll.Question, legacyQuestion.Text);
        Assert.Equal([0, 1, 2], poll.Options.Select(option => option.Position));
        Assert.Equal(
            ["Очный", "Дистанционный", "Гибридный"],
            poll.Options.Select(option => option.Text));

        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Poll storedPoll = Assert.Single(dbContext.Polls);
        Assert.Equal(teacher.Id, storedPoll.CreatorId);
        Assert.Equal(PollStatus.Draft, storedPoll.Status);
        Assert.Equal(3, dbContext.PollOptions.Count());
    }

    [Fact]
    public async Task Create_WithMultipleQuestions_ReturnsOrderedQuestionHierarchy()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/polls",
            new
            {
                Title = "  Обратная связь   по лекции ",
                Questions = new object[]
                {
                    new
                    {
                        Text = " Насколько   понятен материал? ",
                        Options = new[] { "Полностью", " Частично ", "Непонятен" },
                        AllowsMultipleAnswers = true
                    },
                    new
                    {
                        Text = "Подходит ли темп занятий?",
                        Options = new[] { "Да", "Нет" }
                    }
                }
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        PollResponse? poll = await response.Content.ReadFromJsonAsync<PollResponse>();
        Assert.NotNull(poll);
        Assert.Equal("Обратная связь по лекции", poll.Title);
        Assert.Equal("draft", poll.Status);
        Assert.Equal([0, 1], poll.Questions.Select(question => question.Position));
        Assert.Equal(
            ["Насколько понятен материал?", "Подходит ли темп занятий?"],
            poll.Questions.Select(question => question.Text));
        Assert.Equal(
            ["Полностью", "Частично", "Непонятен"],
            poll.Questions[0].Options.Select(option => option.Text));
        Assert.Equal([0, 1, 2], poll.Questions[0].Options.Select(option => option.Position));
        Assert.True(poll.Questions[0].AllowsMultipleAnswers);
        Assert.False(poll.Questions[1].AllowsMultipleAnswers);
        Assert.Null(poll.Question);
        Assert.Empty(poll.Options);

        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, dbContext.PollQuestions.Count());
        Assert.Single(dbContext.PollQuestions, question => question.AllowsMultipleAnswers);
        Assert.Equal(5, dbContext.PollOptions.Count());
    }

    [Fact]
    public async Task Create_MixedPoll_ReturnsTypesAndKeepsKnowledgeSettingsPrivateFromPublicApi()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/polls", new
        {
            title = "Mixed",
            questions = new object[]
            {
                new { text = "Choice", type = "single_choice", options = new[] { "A", "B" } },
                new { text = "Feedback", type = "text", textPurpose = "feedback", options = Array.Empty<string>() },
                new
                {
                    text = "Knowledge", type = "text", textPurpose = "knowledge",
                    knowledgeSettings = new { expectedAnswer = "Expected", gradingCriteria = "Criteria", maxScore = 10 }
                }
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        PollResponse poll = (await response.Content.ReadFromJsonAsync<PollResponse>())!;
        Assert.Equal(["single_choice", "text", "text"], poll.Questions.Select(q => q.Type));
        Assert.Empty(poll.Questions[1].Options);
        Assert.NotNull(poll.Questions[2].KnowledgeSettings);
        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, db.PollQuestions.Count(q => q.Type == PollQuestionType.Text));
        Assert.Single(db.PollQuestionKnowledgeSettings);
    }

    [Fact]
    public async Task Create_ConflictingTypeAndLegacyFlag_Returns400()
    {
        await AssertInvalidRequestAsync(new
        {
            questions = new[]
            {
                new { text = "Question", type = "multiple_choice", allowsMultipleAnswers = false,
                    options = new[] { "A", "B" } }
            }
        }, "Questions[0].Type");
    }

    [Fact]
    public async Task Create_WithoutQuestions_ReturnsValidationProblem()
    {
        await AssertInvalidRequestAsync(
            new { Title = "No questions", Questions = Array.Empty<object>() },
            "Questions");
    }

    [Fact]
    public async Task Create_WithInvalidNestedQuestion_ReturnsValidationProblem()
    {
        await AssertInvalidRequestAsync(
            new
            {
                Questions = new[]
                {
                    new { Text = " ", Options = new[] { "One", "ONE" } }
                }
            },
            "Questions[0].Text");
    }

    [Fact]
    public async Task Create_WithNullQuestionElement_ReturnsValidationProblem()
    {
        await AssertInvalidRequestAsync(
            new { Questions = new object?[] { null } },
            "Questions[0]");
    }

    [Fact]
    public async Task Create_WithOtherMalformedNestedStructures_ReturnsBadRequest()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);
        string[] invalidPayloads =
        [
            """{"questions":[{"text":"Question","options":null}]}""",
            """{"questions":[{"text":"Question","options":["One",null]}]}""",
            """{"questions":[{"text":"Question","options":[{"text":"invalid"},"Two"]}]}""",
            """{"questions":"not-an-array"}"""
        ];

        foreach (string payload in invalidPayloads)
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await client.PostAsync("/api/polls", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(dbContext.Polls);
        Assert.Empty(dbContext.PollQuestions);
        Assert.Empty(dbContext.PollOptions);
    }

    [Fact]
    public async Task Create_WithOneOption_ReturnsValidationProblem()
    {
        await AssertInvalidRequestAsync(
            new { Question = "Question", Options = new[] { "Only one" } },
            "Options");
    }

    [Fact]
    public async Task Create_WithBlankQuestion_ReturnsValidationProblem()
    {
        await AssertInvalidRequestAsync(
            new { Question = " \t ", Options = new[] { "One", "Two" } },
            "Question");
    }

    [Fact]
    public async Task Create_WithBlankOption_ReturnsValidationProblem()
    {
        await AssertInvalidRequestAsync(
            new { Question = "Question", Options = new[] { "One", "  " } },
            "Options[1]");
    }

    [Fact]
    public async Task Create_WithDuplicateNormalizedOption_ReturnsValidationProblem()
    {
        await AssertInvalidRequestAsync(
            new
            {
                Question = "Question",
                Options = new[] { "Hybrid format", "  HYBRID   FORMAT " }
            },
            "Options[1]");
    }

    [Fact]
    public async Task PollEndpoints_WithoutAuthentication_ReturnUnauthorized()
    {
        await using var factory = new AuthWebApplicationFactory();
        using HttpClient client = factory.CreateHttpsClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/polls",
            ValidRequest());
        HttpResponseMessage getResponse = await client.GetAsync("/api/polls");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, getResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            getResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PollEndpoints_WithAdminRoleButWithoutTeacherRole_ReturnForbidden()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser user = await factory.CreateUserAsync(
            "admin@example.test",
            RoleNames.Admin);
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, user);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/polls",
            ValidRequest());
        HttpResponseMessage getResponse = await client.GetAsync("/api/polls");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, getResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            getResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetAll_AndGetById_ReturnOnlyCurrentTeachersPolls()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser firstTeacher = await factory.CreateUserAsync(
            "first-teacher@example.test",
            RoleNames.Teacher);
        SeededUser secondTeacher = await factory.CreateUserAsync(
            "second-teacher@example.test",
            RoleNames.Teacher);
        using HttpClient firstClient = factory.CreateHttpsClient();
        using HttpClient secondClient = factory.CreateHttpsClient();
        await LoginAsync(firstClient, firstTeacher);
        await LoginAsync(secondClient, secondTeacher);

        PollResponse firstPoll = await CreatePollAsync(firstClient, "First poll");
        await Task.Delay(10);
        PollResponse newestFirstPoll = await CreatePollAsync(firstClient, "Newest poll");
        PollResponse secondPoll = await CreatePollAsync(secondClient, "Foreign poll");

        HttpResponseMessage listResponse = await firstClient.GetAsync("/api/polls");
        IReadOnlyList<PollResponse>? polls =
            await listResponse.Content.ReadFromJsonAsync<IReadOnlyList<PollResponse>>();

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.NotNull(polls);
        Assert.Equal([newestFirstPoll.Id, firstPoll.Id], polls.Select(poll => poll.Id));
        Assert.All(polls, poll => Assert.Equal("draft", poll.Status));

        HttpResponseMessage ownResponse =
            await firstClient.GetAsync($"/api/polls/{firstPoll.Id}");
        PollResponse? ownPoll =
            await ownResponse.Content.ReadFromJsonAsync<PollResponse>();
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.NotNull(ownPoll);
        Assert.Equal("draft", ownPoll.Status);
        Assert.Equal([0, 1], ownPoll.Options.Select(option => option.Position));

        HttpResponseMessage foreignResponse =
            await firstClient.GetAsync($"/api/polls/{secondPoll.Id}");
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task PublishAndClose_ValidDraft_UpdatesStatusAndUtcTimestamps()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);
        PollResponse draft = await CreatePollAsync(client, "Lifecycle poll");

        HttpResponseMessage publishResponse =
            await client.PostAsync($"/api/polls/{draft.Id}/publish", null);
        PollResponse? open = await publishResponse.Content.ReadFromJsonAsync<PollResponse>();

        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.NotNull(open);
        Assert.Equal("open", open.Status);
        Assert.NotNull(open.PublishedAt);
        Assert.Equal(DateTimeKind.Utc, open.PublishedAt.Value.Kind);
        Assert.Null(open.ClosedAt);

        HttpResponseMessage closeResponse =
            await client.PostAsync($"/api/polls/{draft.Id}/close", null);
        PollResponse? closed = await closeResponse.Content.ReadFromJsonAsync<PollResponse>();

        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        Assert.NotNull(closed);
        Assert.Equal("closed", closed.Status);
        Assert.Equal(open.PublishedAt, closed.PublishedAt);
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal(DateTimeKind.Utc, closed.ClosedAt.Value.Kind);
    }

    [Fact]
    public async Task StatusTransitions_WithInvalidState_ReturnConflictProblemDetails()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);
        PollResponse poll = await CreatePollAsync(client, "Conflict poll");

        HttpResponseMessage closeDraftResponse =
            await client.PostAsync($"/api/polls/{poll.Id}/close", null);
        HttpResponseMessage publishResponse =
            await client.PostAsync($"/api/polls/{poll.Id}/publish", null);
        HttpResponseMessage republishResponse =
            await client.PostAsync($"/api/polls/{poll.Id}/publish", null);

        AssertConflict(closeDraftResponse);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        AssertConflict(republishResponse);
    }

    [Fact]
    public async Task Publish_WithInvalidPersistedQuestionStructure_ReturnsConflict()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        Guid invalidPollId = Guid.NewGuid();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ApplicationDbContext dbContext =
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.Polls.Add(new Poll
            {
                Id = invalidPollId,
                CreatorId = teacher.Id,
                Title = "Invalid persisted poll",
                Status = PollStatus.Draft,
                CreatedAt = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage response =
            await client.PostAsync($"/api/polls/{invalidPollId}/publish", null);

        AssertConflict(response);
    }

    [Fact]
    public async Task StatusTransitions_HideForeignPollButAllowAdministrator()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser owner = await factory.CreateUserAsync(
            "owner@example.test",
            RoleNames.Teacher);
        SeededUser otherTeacher = await factory.CreateUserAsync(
            "other@example.test",
            RoleNames.Teacher);
        SeededUser admin = await factory.CreateUserAsync(
            "admin-transition@example.test",
            RoleNames.Admin);
        using HttpClient ownerClient = factory.CreateHttpsClient();
        using HttpClient otherClient = factory.CreateHttpsClient();
        using HttpClient adminClient = factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(otherClient, otherTeacher);
        await LoginAsync(adminClient, admin);
        PollResponse poll = await CreatePollAsync(ownerClient, "Owned poll");

        HttpResponseMessage foreignResponse =
            await otherClient.PostAsync($"/api/polls/{poll.Id}/publish", null);
        HttpResponseMessage adminResponse =
            await adminClient.PostAsync($"/api/polls/{poll.Id}/publish", null);

        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        Assert.Equal("application/problem+json", foreignResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        PollResponse? published = await adminResponse.Content.ReadFromJsonAsync<PollResponse>();
        Assert.Equal("open", published?.Status);
    }

    [Fact]
    public async Task Create_WithMalformedOrEmptyJson_ReturnsBadRequest()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        using var malformedContent = new StringContent(
            "{",
            Encoding.UTF8,
            "application/json");
        using var emptyContent = new StringContent(
            string.Empty,
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage malformedResponse =
            await client.PostAsync("/api/polls", malformedContent);
        HttpResponseMessage emptyResponse =
            await client.PostAsync("/api/polls", emptyContent);

        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            malformedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "application/problem+json",
            emptyResponse.Content.Headers.ContentType?.MediaType);

        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(dbContext.Polls);
        Assert.Empty(dbContext.PollQuestions);
        Assert.Empty(dbContext.PollOptions);
    }

    private static async Task AssertInvalidRequestAsync(object request, string errorKey)
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser teacher = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/polls", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(
            errorKey,
            out _));

        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(dbContext.Polls);
        Assert.Empty(dbContext.PollOptions);
    }

    private static async Task<PollResponse> CreatePollAsync(
        HttpClient client,
        string question)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/polls",
            new { Question = question, Options = new[] { "First", "Second" } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PollResponse>())!;
    }

    private static object ValidRequest()
    {
        return new { Question = "Question", Options = new[] { "One", "Two" } };
    }

    private static void AssertConflict(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password });
        response.EnsureSuccessStatusCode();
    }
}
