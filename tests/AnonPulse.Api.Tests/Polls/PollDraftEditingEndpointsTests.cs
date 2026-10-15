using System.Net;
using System.Net.Http.Json;
using AnonPulse.Api.Contracts.Polls;
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

public sealed class PollDraftEditingEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_poll_edit_tests")
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
    public async Task Owner_ReplacesEntireDraftAndPreservesPollMetadata()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, owner);
        PollResponse created = await CreateDraftAsync(client, "Original question");
        PollResponse original = Assert.IsType<PollResponse>(
            await client.GetFromJsonAsync<PollResponse>($"/api/polls/{created.Id}"));
        Guid originalQuestionId = Assert.Single(original.Questions).Id;
        Guid[] originalOptionIds = original.Options.Select(option => option.Id).ToArray();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/polls/{original.Id}",
            new
            {
                Title = "  Updated   title ",
                Questions = new object[]
                {
                    new
                    {
                        Text = " Second question ",
                        Options = new[] { "Second A", "Second B", "Second C" },
                        AllowsMultipleAnswers = true
                    },
                    new
                    {
                        Text = " First question ",
                        Options = new[] { "First A", "First B" }
                    }
                }
            });
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200, got {(int)response.StatusCode}: {responseBody}");
        PollResponse? updated = await response.Content.ReadFromJsonAsync<PollResponse>();

        Assert.NotNull(updated);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.CreatedAt, updated.CreatedAt);
        Assert.Equal("draft", updated.Status);
        Assert.Equal("Updated title", updated.Title);
        Assert.Equal([0, 1], updated.Questions.Select(question => question.Position));
        Assert.Equal(
            ["Second question", "First question"],
            updated.Questions.Select(question => question.Text));
        Assert.Equal(
            [0, 1, 2],
            updated.Questions[0].Options.Select(option => option.Position));
        Assert.True(updated.Questions[0].AllowsMultipleAnswers);
        Assert.False(updated.Questions[1].AllowsMultipleAnswers);
        Assert.DoesNotContain(originalQuestionId, updated.Questions.Select(item => item.Id));
        Assert.Empty(updated.Questions
            .SelectMany(question => question.Options)
            .Select(option => option.Id)
            .Intersect(originalOptionIds));

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Poll stored = await db.Polls
            .Include(poll => poll.Questions)
            .ThenInclude(question => question.Options)
            .SingleAsync(poll => poll.Id == original.Id);
        Assert.Equal(owner.Id, stored.CreatorId);
        Assert.Equal(original.CreatedAt, stored.CreatedAt);
        Assert.Equal(2, stored.Questions.Count);
        Assert.Single(stored.Questions, question => question.AllowsMultipleAnswers);
        Assert.Equal(5, stored.Questions.SelectMany(question => question.Options).Count());
    }

    [Fact]
    public async Task InvalidReplacement_ReturnsBadRequestWithoutPartialChanges()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, owner);
        PollResponse original = await CreateDraftAsync(client, "Stable question");

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/polls/{original.Id}",
            new
            {
                Title = "Must not persist",
                Questions = new[]
                {
                    new { Text = "Invalid", Options = new[] { "Only one" } }
                }
            });
        PollResponse? unchanged = await client.GetFromJsonAsync<PollResponse>(
            $"/api/polls/{original.Id}");

        AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.NotNull(unchanged);
        Assert.Equal(original.Title, unchanged.Title);
        Assert.Equal(
            original.Questions.Select(question =>
                (question.Id, question.Text, question.Position)),
            unchanged.Questions.Select(question =>
                (question.Id, question.Text, question.Position)));
        Assert.Equal(
            original.Questions.SelectMany(question => question.Options.Select(option =>
                (question.Id, option.Id, option.Text, option.Position))),
            unchanged.Questions.SelectMany(question => question.Options.Select(option =>
                (question.Id, option.Id, option.Text, option.Position))));
    }

    [Fact]
    public async Task OpenAndClosedPolls_CannotBeEdited()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, owner);
        PollResponse poll = await CreateDraftAsync(client, "Lifecycle question");
        HttpResponseMessage publish =
            await client.PostAsync($"/api/polls/{poll.Id}/publish", null);

        HttpResponseMessage openEdit = await client.PutAsJsonAsync(
            $"/api/polls/{poll.Id}",
            Replacement("Open edit"));
        HttpResponseMessage close =
            await client.PostAsync($"/api/polls/{poll.Id}/close", null);
        HttpResponseMessage closedEdit = await client.PutAsJsonAsync(
            $"/api/polls/{poll.Id}",
            Replacement("Closed edit"));

        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        AssertProblem(openEdit, HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        AssertProblem(closedEdit, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AnonymousAndForeignTeacher_CannotEditDraft()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser foreignTeacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient foreignClient = _factory.CreateHttpsClient();
        using HttpClient anonymousClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(foreignClient, foreignTeacher);
        PollResponse poll = await CreateDraftAsync(ownerClient, "Private draft");

        HttpResponseMessage anonymous = await anonymousClient.PutAsJsonAsync(
            $"/api/polls/{poll.Id}",
            Replacement("Anonymous edit"));
        HttpResponseMessage foreign = await foreignClient.PutAsJsonAsync(
            $"/api/polls/{poll.Id}",
            Replacement("Foreign edit"));

        AssertProblem(anonymous, HttpStatusCode.Unauthorized);
        AssertProblem(foreign, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ConcurrentEditAndPublish_AreSerializedWithoutEditingOpenPoll()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient editClient = _factory.CreateHttpsClient();
        using HttpClient publishClient = _factory.CreateHttpsClient();
        await LoginAsync(editClient, owner);
        await LoginAsync(publishClient, owner);
        PollResponse poll = await CreateDraftAsync(editClient, "Original concurrent question");

        Task<HttpResponseMessage> editTask = editClient.PutAsJsonAsync(
            $"/api/polls/{poll.Id}",
            Replacement("Edited concurrent question"));
        Task<HttpResponseMessage> publishTask =
            publishClient.PostAsync($"/api/polls/{poll.Id}/publish", null);
        await Task.WhenAll(editTask, publishTask);
        using HttpResponseMessage editResponse = await editTask;
        using HttpResponseMessage publishResponse = await publishTask;

        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.Contains(
            editResponse.StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Poll stored = await db.Polls
            .AsNoTracking()
            .Include(item => item.Questions)
            .SingleAsync(item => item.Id == poll.Id);
        Assert.Equal(PollStatus.Open, stored.Status);
        Assert.Equal(
            editResponse.StatusCode == HttpStatusCode.OK
                ? "Edited concurrent question"
                : "Original concurrent question",
            Assert.Single(stored.Questions).Text);
    }

    private async Task<SeededUser> CreateUserAsync(string role)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roleManager.RoleExistsAsync(role))
        {
            IdentityResult roleResult =
                await roleManager.CreateAsync(new IdentityRole<Guid>(role));
            Assert.True(roleResult.Succeeded);
        }

        string email = $"poll-edit-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        const string password = "Poll-edit-password-123!";
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded);
        IdentityResult roleResultAssignment = await userManager.AddToRoleAsync(user, role);
        Assert.True(roleResultAssignment.Succeeded);
        return new SeededUser(user.Id, email, password);
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<PollResponse> CreateDraftAsync(
        HttpClient client,
        string question)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/polls",
            new
            {
                Title = "Original title",
                Question = question,
                Options = new[] { "First", "Second" }
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<PollResponse>(
            await response.Content.ReadFromJsonAsync<PollResponse>());
    }

    private static object Replacement(string question) => new
    {
        Title = "Replacement title",
        Questions = new[]
        {
            new { Text = question, Options = new[] { "Yes", "No" } }
        }
    };

    private static void AssertProblem(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }
}
