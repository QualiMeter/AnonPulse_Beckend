using System.Net;
using System.Net.Http.Json;
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

public sealed class IdentifiedPollEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_identified_tests")
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
    public async Task Create_DefaultsToAnonymous_AndDraftModeCanChange()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        PollResponse anonymous = await CreatePollAsync(client, responseMode: null);
        PollResponse identified = await CreatePollAsync(client, "identified");
        HttpResponseMessage invalid = await client.PostAsJsonAsync(
            "/api/polls",
            MixedPollRequest("named"));
        HttpResponseMessage update = await client.PutAsJsonAsync(
            $"/api/polls/{anonymous.Id}",
            MixedPollRequest("identified"));
        PollResponse? updated = await update.Content.ReadFromJsonAsync<PollResponse>();

        Assert.Equal("anonymous", anonymous.ResponseMode);
        Assert.Equal("identified", identified.ResponseMode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("identified", updated?.ResponseMode);
    }

    [Fact]
    public async Task ResponseMode_CannotChangeAfterPublication()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, teacher);
        PollResponse poll = await CreatePollAsync(client, "identified");
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/polls/{poll.Id}/publish", null)).StatusCode);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/polls/{poll.Id}",
            MixedPollRequest("anonymous"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using IServiceScope scope = _factory.Services.CreateScope();
        Poll stored = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Polls.AsNoTracking().SingleAsync(item => item.Id == poll.Id);
        Assert.Equal(PollResponseMode.Identified, stored.ResponseMode);
    }

    [Fact]
    public async Task IdentifiedVoting_StoresOneAtomicMixedSubmission_AndAllowsSameNames()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient teacherClient = _factory.CreateHttpsClient();
        await LoginAsync(teacherClient, teacher);
        PollResponse poll = await CreatePollAsync(teacherClient, "identified");
        await PublishAsync(teacherClient, poll.Id);
        PublicPollResponse publicPoll = await GetPublicPollAsync(poll.Id);
        Assert.Equal("identified", publicPoll.ResponseMode);

        foreach (int suffix in new[] { 1, 2 })
        {
            using HttpClient voter = _factory.CreateHttpsClient();
            await voter.GetAsync($"/api/public/polls/{poll.Id}");
            HttpResponseMessage vote = await SubmitAsync(
                voter,
                poll.Id,
                publicPoll,
                "  Иванов   Иван  ",
                $"Ответ {suffix}");
            Assert.Equal(HttpStatusCode.NoContent, vote.StatusCode);
        }

        HttpResponseMessage beforeClose =
            await teacherClient.GetAsync($"/api/polls/{poll.Id}/submissions");
        Assert.Equal(HttpStatusCode.Conflict, beforeClose.StatusCode);
        await CloseAsync(teacherClient, poll.Id);

        HttpResponseMessage response = await teacherClient.GetAsync(
            $"/api/polls/{poll.Id}/submissions?page=1&pageSize=1");
        PagedResponse<IdentifiedPollSubmissionResponse>? page =
            await response.Content.ReadFromJsonAsync<
                PagedResponse<IdentifiedPollSubmissionResponse>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        IdentifiedPollSubmissionResponse item = Assert.Single(page.Items);
        Assert.Equal("Иванов Иван", item.RespondentName);
        Assert.Equal(4, item.Answers.Count);
        Assert.Single(item.Answers[0].SelectedOptionIds);
        Assert.Equal(2, item.Answers[1].SelectedOptionIds.Count);
        Assert.NotNull(item.Answers[2].Text);
        Assert.NotNull(item.Answers[3].Text);
        string aggregateResults = await teacherClient.GetStringAsync(
            $"/api/polls/{poll.Id}/results");
        Assert.DoesNotContain("Иванов", aggregateResults, StringComparison.Ordinal);

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, await db.IdentifiedPollSubmissions.CountAsync());
        Assert.Equal(6, await db.Votes.CountAsync(vote => vote.IdentifiedSubmissionId != null));
        Assert.Equal(4, await db.PollTextAnswers.CountAsync(
            answer => answer.IdentifiedSubmissionId != null));
    }

    [Fact]
    public async Task NameRules_RejectInvalidRequestsWithoutPartialPersistence()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient teacherClient = _factory.CreateHttpsClient();
        await LoginAsync(teacherClient, teacher);
        PollResponse identified = await CreatePollAsync(teacherClient, "identified");
        PollResponse anonymous = await CreatePollAsync(teacherClient, null);
        await PublishAsync(teacherClient, identified.Id);
        await PublishAsync(teacherClient, anonymous.Id);
        PublicPollResponse identifiedPublic = await GetPublicPollAsync(identified.Id);
        PublicPollResponse anonymousPublic = await GetPublicPollAsync(anonymous.Id);

        using HttpClient missingName = _factory.CreateHttpsClient();
        await missingName.GetAsync($"/api/public/polls/{identified.Id}");
        HttpResponseMessage missing = await SubmitAsync(
            missingName, identified.Id, identifiedPublic, null, "text");

        using HttpClient blankName = _factory.CreateHttpsClient();
        await blankName.GetAsync($"/api/public/polls/{identified.Id}");
        HttpResponseMessage blank = await SubmitAsync(
            blankName, identified.Id, identifiedPublic, " \t ", "text");

        using HttpClient longName = _factory.CreateHttpsClient();
        await longName.GetAsync($"/api/public/polls/{identified.Id}");
        HttpResponseMessage tooLong = await SubmitAsync(
            longName, identified.Id, identifiedPublic, new string('x', 151), "text");

        using HttpClient namedAnonymous = _factory.CreateHttpsClient();
        await namedAnonymous.GetAsync($"/api/public/polls/{anonymous.Id}");
        HttpResponseMessage forbiddenName = await SubmitAsync(
            namedAnonymous, anonymous.Id, anonymousPublic, "Name", "text");

        using HttpClient incompleteAnswers = _factory.CreateHttpsClient();
        await incompleteAnswers.GetAsync($"/api/public/polls/{identified.Id}");
        using var incompleteRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/public/polls/{identified.Id}/votes")
        {
            Content = JsonContent.Create(new
            {
                RespondentName = "Valid name",
                Answers = new[]
                {
                    new
                    {
                        QuestionId = identifiedPublic.Questions[0].Id,
                        OptionId = identifiedPublic.Questions[0].Options[0].Id
                    }
                }
            })
        };
        incompleteRequest.Headers.Add("X-AnonPulse-Vote", "1");
        HttpResponseMessage incomplete = await incompleteAnswers.SendAsync(incompleteRequest);

        Assert.All(new[] { missing, blank, tooLong, forbiddenName, incomplete }, item =>
            Assert.Equal(HttpStatusCode.BadRequest, item.StatusCode));
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(db.IdentifiedPollSubmissions);
        Assert.Empty(db.Votes);
        Assert.Empty(db.PollTextAnswers);
        Assert.Empty(db.VoteParticipations);

        using HttpClient maximumName = _factory.CreateHttpsClient();
        await maximumName.GetAsync($"/api/public/polls/{identified.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await SubmitAsync(
            maximumName,
            identified.Id,
            identifiedPublic,
            new string('я', 150),
            "text")).StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal(150, (await db.IdentifiedPollSubmissions
            .AsNoTracking()
            .SingleAsync()).RespondentName.Length);
    }

    [Fact]
    public async Task DuplicateToken_IsRejectedAndLeavesOneCompleteSubmission()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient teacherClient = _factory.CreateHttpsClient();
        await LoginAsync(teacherClient, teacher);
        PollResponse poll = await CreatePollAsync(teacherClient, "identified");
        await PublishAsync(teacherClient, poll.Id);
        PublicPollResponse publicPoll = await GetPublicPollAsync(poll.Id);
        using HttpClient voter = _factory.CreateHttpsClient();
        await voter.GetAsync($"/api/public/polls/{poll.Id}");

        HttpResponseMessage first = await SubmitAsync(
            voter, poll.Id, publicPoll, "Same browser", "First");
        HttpResponseMessage second = await SubmitAsync(
            voter, poll.Id, publicPoll, "Same browser", "Second");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(db.IdentifiedPollSubmissions);
        Assert.Single(db.VoteParticipations);
        Assert.Equal(3, await db.Votes.CountAsync());
        Assert.Equal(2, await db.PollTextAnswers.CountAsync());
    }

    [Fact]
    public async Task Submissions_ArePrivateClosedOnlyIdentifiedAndPaged()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser stranger = await CreateUserAsync(RoleNames.Teacher);
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient strangerClient = _factory.CreateHttpsClient();
        using HttpClient adminClient = _factory.CreateHttpsClient();
        using HttpClient anonymousClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(strangerClient, stranger);
        await LoginAsync(adminClient, admin);
        PollResponse identified = await CreatePollAsync(ownerClient, "identified");
        PollResponse anonymous = await CreatePollAsync(ownerClient, null);
        await PublishAsync(ownerClient, identified.Id);
        await CloseAsync(ownerClient, identified.Id);
        await PublishAsync(ownerClient, anonymous.Id);
        await CloseAsync(ownerClient, anonymous.Id);

        HttpResponseMessage unauthenticated =
            await anonymousClient.GetAsync($"/api/polls/{identified.Id}/submissions");
        HttpResponseMessage foreign =
            await strangerClient.GetAsync($"/api/polls/{identified.Id}/submissions");
        HttpResponseMessage adminResponse =
            await adminClient.GetAsync($"/api/polls/{identified.Id}/submissions");
        AdminPollDetailsResponse? adminPoll = await adminClient.GetFromJsonAsync<
            AdminPollDetailsResponse>($"/api/admin/polls/{identified.Id}");
        HttpResponseMessage anonymousPoll =
            await ownerClient.GetAsync($"/api/polls/{anonymous.Id}/submissions");
        HttpResponseMessage badPage = await ownerClient.GetAsync(
            $"/api/polls/{identified.Id}/submissions?page=0&pageSize=101");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        Assert.Equal("identified", adminPoll?.ResponseMode);
        Assert.Equal(HttpStatusCode.Conflict, anonymousPoll.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    [Fact]
    public async Task AnonymousVoting_StaysUnlinked_AndAdminDeleteCascadesIdentifiedData()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        using HttpClient teacherClient = _factory.CreateHttpsClient();
        using HttpClient adminClient = _factory.CreateHttpsClient();
        await LoginAsync(teacherClient, teacher);
        await LoginAsync(adminClient, admin);
        PollResponse anonymous = await CreatePollAsync(teacherClient, null);
        await PublishAsync(teacherClient, anonymous.Id);
        PublicPollResponse publicPoll = await GetPublicPollAsync(anonymous.Id);
        using HttpClient voter = _factory.CreateHttpsClient();
        await voter.GetAsync($"/api/public/polls/{anonymous.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await SubmitAsync(
            voter, anonymous.Id, publicPoll, null, "Anonymous text")).StatusCode);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ApplicationDbContext db = scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>();
            Assert.Empty(db.IdentifiedPollSubmissions);
            Assert.All(await db.Votes.ToListAsync(), vote =>
                Assert.Null(vote.IdentifiedSubmissionId));
            Assert.All(await db.PollTextAnswers.ToListAsync(), answer =>
                Assert.Null(answer.IdentifiedSubmissionId));
        }

        PollResponse identified = await CreatePollAsync(teacherClient, "identified");
        await PublishAsync(teacherClient, identified.Id);
        PublicPollResponse identifiedPublic = await GetPublicPollAsync(identified.Id);
        using HttpClient identifiedVoter = _factory.CreateHttpsClient();
        await identifiedVoter.GetAsync($"/api/public/polls/{identified.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await SubmitAsync(
            identifiedVoter,
            identified.Id,
            identifiedPublic,
            "Delete me",
            "Identified text")).StatusCode);

        string csrf = (await adminClient.GetFromJsonAsync<CsrfTokenResponse>(
            "/api/auth/csrf-token"))!.Token;
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/admin/polls/{identified.Id}?confirm=true");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent,
            (await adminClient.SendAsync(request)).StatusCode);

        using IServiceScope verificationScope = _factory.Services.CreateScope();
        ApplicationDbContext verificationDb = verificationScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        Assert.False(await verificationDb.Polls.AnyAsync(item => item.Id == identified.Id));
        Assert.Empty(verificationDb.IdentifiedPollSubmissions);
        Assert.Equal(3, await verificationDb.Votes.CountAsync());
        Assert.Equal(2, await verificationDb.PollTextAnswers.CountAsync());
        Assert.Single(verificationDb.VoteParticipations);
        Assert.All(await verificationDb.Votes.ToListAsync(), vote =>
            Assert.Null(vote.IdentifiedSubmissionId));
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

        string email = $"identified-{Guid.NewGuid():N}@example.test";
        const string password = "Identified-password-123!";
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

    private static async Task LoginAsync(HttpClient client, SeededUser user)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static object MixedPollRequest(string? responseMode) => new
    {
        Title = "Mixed poll",
        ResponseMode = responseMode,
        Questions = new object[]
        {
            new { Text = "Single", Type = "single_choice", Options = new[] { "A", "B" } },
            new { Text = "Multiple", Type = "multiple_choice", Options = new[] { "C", "D", "E" } },
            new { Text = "Feedback", Type = "text", TextPurpose = "feedback", Options = Array.Empty<string>() },
            new
            {
                Text = "Knowledge",
                Type = "text",
                TextPurpose = "knowledge",
                Options = Array.Empty<string>(),
                KnowledgeSettings = new
                {
                    ExpectedAnswer = "Expected",
                    GradingCriteria = "Criteria",
                    MaxScore = 10
                }
            }
        }
    };

    private static async Task<PollResponse> CreatePollAsync(
        HttpClient client,
        string? responseMode)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/polls",
            MixedPollRequest(responseMode));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PollResponse>())!;
    }

    private static async Task PublishAsync(HttpClient client, Guid pollId) =>
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/polls/{pollId}/publish", null)).StatusCode);

    private static async Task CloseAsync(HttpClient client, Guid pollId) =>
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/polls/{pollId}/close", null)).StatusCode);

    private async Task<PublicPollResponse> GetPublicPollAsync(Guid pollId)
    {
        using HttpClient client = _factory.CreateHttpsClient();
        return (await client.GetFromJsonAsync<PublicPollResponse>(
            $"/api/public/polls/{pollId}"))!;
    }

    private static Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        Guid pollId,
        PublicPollResponse poll,
        string? respondentName,
        string text)
    {
        PublicPollQuestionResponse single = poll.Questions[0];
        PublicPollQuestionResponse multiple = poll.Questions[1];
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/public/polls/{pollId}/votes")
        {
            Content = JsonContent.Create(new
            {
                RespondentName = respondentName,
                Answers = new object[]
                {
                    new { QuestionId = single.Id, OptionId = single.Options[0].Id },
                    new { QuestionId = multiple.Id, OptionId = multiple.Options[0].Id },
                    new { QuestionId = multiple.Id, OptionId = multiple.Options[2].Id },
                    new { QuestionId = poll.Questions[2].Id, Text = text },
                    new { QuestionId = poll.Questions[3].Id, Text = text }
                }
            })
        };
        request.Headers.Add("X-AnonPulse-Vote", "1");
        return client.SendAsync(request);
    }
}
