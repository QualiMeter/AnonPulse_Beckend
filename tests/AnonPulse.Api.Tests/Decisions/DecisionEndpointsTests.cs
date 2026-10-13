using System.Net;
using System.Net.Http.Json;
using AnonPulse.Api.Contracts.Decisions;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Tests.Auth;
using AnonPulse.Api.Tests.Voting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Decisions;

public sealed class DecisionEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_decision_tests")
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
    public async Task Owner_CreatesDecisionWithServerControlledAuthor()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser other = await CreateUserAsync(RoleNames.Teacher);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Closed);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, owner);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new
            {
                Text = "  Materials will be available earlier.  ",
                AuthorId = other.Id
            });
        DecisionResponse? decision =
            await response.Content.ReadFromJsonAsync<DecisionResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(decision);
        Assert.Equal(pollId, decision.PollId);
        Assert.Equal("Materials will be available earlier.", decision.Text);
        Assert.Equal(DateTimeKind.Utc, decision.PublishedAt.Kind);
        using IServiceScope scope = _factory.Services.CreateScope();
        Decision stored = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .Decisions
            .SingleAsync(item => item.PollId == pollId);
        Assert.Equal(owner.Id, stored.AuthorId);
    }

    [Fact]
    public async Task RepeatedPut_UpdatesOneRowAndUnchangedTextPreservesTimestampAndAuthor()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Closed);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient adminClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(adminClient, admin);

        DecisionResponse first = await PutDecisionAsync(ownerClient, pollId, "First text");
        await Task.Delay(20);
        DecisionResponse updated = await PutDecisionAsync(adminClient, pollId, "Updated text");
        await Task.Delay(20);
        DecisionResponse unchanged =
            await PutDecisionAsync(adminClient, pollId, "  Updated text  ");

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal(updated.Id, unchanged.Id);
        Assert.True(updated.PublishedAt > first.PublishedAt);
        Assert.Equal(updated.PublishedAt, unchanged.PublishedAt);
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Decision stored = await db.Decisions.SingleAsync(item => item.PollId == pollId);
        Assert.Equal(owner.Id, stored.AuthorId);
        Assert.Equal("Updated text", stored.Text);
    }

    [Fact]
    public async Task Administrator_CanCreateDecisionForClosedPoll()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Closed);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);

        DecisionResponse decision =
            await PutDecisionAsync(client, pollId, "Administrative decision");

        Assert.Equal("Administrative decision", decision.Text);
        using IServiceScope scope = _factory.Services.CreateScope();
        Decision stored = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .Decisions.SingleAsync(item => item.PollId == pollId);
        Assert.Equal(admin.Id, stored.AuthorId);
    }

    [Fact]
    public async Task OwnerAndAdministrator_CannotPublishDecisionForOpenPoll()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Open);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient adminClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(adminClient, admin);

        HttpResponseMessage ownerResponse = await ownerClient.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new { Text = "Owner decision" });
        HttpResponseMessage adminResponse = await adminClient.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new { Text = "Admin decision" });
        string ownerBody = await ownerResponse.Content.ReadAsStringAsync();

        AssertProblem(ownerResponse, HttpStatusCode.Conflict);
        AssertProblem(adminResponse, HttpStatusCode.Conflict);
        Assert.Contains(
            "only be published after the poll is closed",
            ownerBody,
            StringComparison.Ordinal);
        await AssertDecisionCountAsync(pollId, 0);
    }

    [Fact]
    public async Task ForeignTeacherAnonymousAndWrongRole_AreRejected()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser foreignTeacher = await CreateUserAsync(RoleNames.Teacher);
        SeededUser wrongRole = await CreateUserAsync(role: null);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Closed);
        using HttpClient anonymousClient = _factory.CreateHttpsClient();
        using HttpClient foreignClient = _factory.CreateHttpsClient();
        using HttpClient wrongRoleClient = _factory.CreateHttpsClient();
        await LoginAsync(foreignClient, foreignTeacher);
        await LoginAsync(wrongRoleClient, wrongRole);

        HttpResponseMessage anonymous = await anonymousClient.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new { Text = "Anonymous" });
        HttpResponseMessage foreign = await foreignClient.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new { Text = "Foreign" });
        HttpResponseMessage forbidden = await wrongRoleClient.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new { Text = "Forbidden" });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(
            "application/problem+json",
            anonymous.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "application/problem+json",
            forbidden.Content.Headers.ContentType?.MediaType);
        await AssertDecisionCountAsync(pollId, 0);
    }

    [Fact]
    public async Task MaximumLengthTextWithSurroundingWhitespace_IsAcceptedAndStoredTrimmed()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        Guid pollId = await SeedPollAsync(teacher.Id, PollStatus.Closed);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, teacher);
        string significantText = new('a', DecisionContractLimits.MaximumTextLength);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new { Text = $"  {significantText}\r\n" });
        DecisionResponse? decision =
            await response.Content.ReadFromJsonAsync<DecisionResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(decision);
        Assert.Equal(significantText, decision.Text);
        using IServiceScope scope = _factory.Services.CreateScope();
        Decision stored = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .Decisions.SingleAsync(item => item.PollId == pollId);
        Assert.Equal(significantText, stored.Text);
    }

    [Fact]
    public async Task TextExceedingMaximumSignificantLength_IsRejected()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        Guid pollId = await SeedPollAsync(teacher.Id, PollStatus.Closed);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new
            {
                Text = $"  {new string('a', DecisionContractLimits.MaximumTextLength + 1)}  "
            });

        AssertProblem(response, HttpStatusCode.BadRequest);
        await AssertDecisionCountAsync(pollId, 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public async Task EmptyOrWhitespaceText_IsRejected(string text)
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        Guid pollId = await SeedPollAsync(teacher.Id, PollStatus.Closed);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new { Text = text });

        AssertProblem(response, HttpStatusCode.BadRequest);
        await AssertDecisionCountAsync(pollId, 0);
    }

    [Fact]
    public async Task UnknownPollAndDraft_ReturnExpectedProblems()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        Guid draftPollId = await SeedPollAsync(teacher.Id, PollStatus.Draft);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, teacher);

        HttpResponseMessage missing = await client.PutAsJsonAsync(
            $"/api/polls/{Guid.NewGuid()}/decision",
            new { Text = "Missing" });
        HttpResponseMessage draft = await client.PutAsJsonAsync(
            $"/api/polls/{draftPollId}/decision",
            new { Text = "Draft" });

        AssertProblem(missing, HttpStatusCode.NotFound);
        AssertProblem(draft, HttpStatusCode.Conflict);
        await AssertDecisionCountAsync(draftPollId, 0);
    }

    [Fact]
    public async Task PublicGet_ReturnsOnlyPublishedDecisionAndNoPersonalData()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Closed);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        DecisionResponse created =
            await PutDecisionAsync(ownerClient, pollId, "Public decision");
        using HttpClient publicClient = _factory.CreateHttpsClient();

        HttpResponseMessage response =
            await publicClient.GetAsync($"/api/polls/{pollId}/decision");
        string json = await response.Content.ReadAsStringAsync();
        DecisionResponse? decision =
            await response.Content.ReadFromJsonAsync<DecisionResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(created, decision);
        Assert.DoesNotContain("author", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicGet_HidesMissingDraftAndUnpublishedDecisions()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        Guid missingDecisionPoll = await SeedPollAsync(owner.Id, PollStatus.Open);
        Guid draftPoll = await SeedPollAsync(owner.Id, PollStatus.Draft);
        Guid unpublishedPoll = await SeedPollAsync(owner.Id, PollStatus.Open);
        await SeedDecisionAsync(draftPoll, owner.Id, "Draft secret", DateTime.UtcNow);
        await SeedDecisionAsync(unpublishedPoll, owner.Id, "Unpublished secret", null);
        using HttpClient client = _factory.CreateHttpsClient();

        HttpResponseMessage absent =
            await client.GetAsync($"/api/polls/{missingDecisionPoll}/decision");
        HttpResponseMessage unknown =
            await client.GetAsync($"/api/polls/{Guid.NewGuid()}/decision");
        HttpResponseMessage draft =
            await client.GetAsync($"/api/polls/{draftPoll}/decision");
        string draftBody = await draft.Content.ReadAsStringAsync();
        HttpResponseMessage unpublished =
            await client.GetAsync($"/api/polls/{unpublishedPoll}/decision");
        string unpublishedBody = await unpublished.Content.ReadAsStringAsync();

        AssertProblem(absent, HttpStatusCode.NotFound);
        AssertProblem(unknown, HttpStatusCode.NotFound);
        AssertProblem(draft, HttpStatusCode.NotFound);
        AssertProblem(unpublished, HttpStatusCode.NotFound);
        Assert.DoesNotContain("Draft secret", draftBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Unpublished secret", unpublishedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingDecisionForOpenPoll_IsHiddenUntilPollCloses()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Open);
        await SeedDecisionAsync(
            pollId,
            owner.Id,
            "Previously published decision",
            DateTime.UtcNow);
        using HttpClient client = _factory.CreateHttpsClient();

        HttpResponseMessage whileOpen =
            await client.GetAsync($"/api/polls/{pollId}/decision");
        string whileOpenBody = await whileOpen.Content.ReadAsStringAsync();
        await SetPollStatusAsync(pollId, PollStatus.Closed);
        HttpResponseMessage afterClose =
            await client.GetAsync($"/api/polls/{pollId}/decision");
        DecisionResponse? published =
            await afterClose.Content.ReadFromJsonAsync<DecisionResponse>();

        AssertProblem(whileOpen, HttpStatusCode.NotFound);
        Assert.DoesNotContain(
            "Previously published decision",
            whileOpenBody,
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, afterClose.StatusCode);
        Assert.Equal("Previously published decision", published?.Text);
    }

    [Fact]
    public async Task ConcurrentCloseAndDecision_NeverPublishesWhilePollIsOpen()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Open);
        using HttpClient closeClient = _factory.CreateHttpsClient();
        using HttpClient decisionClient = _factory.CreateHttpsClient();
        await LoginAsync(closeClient, owner);
        await LoginAsync(decisionClient, owner);

        Task<HttpResponseMessage> closeTask =
            closeClient.PostAsync($"/api/polls/{pollId}/close", null);
        Task<HttpResponseMessage> decisionTask = decisionClient.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new { Text = "Concurrent decision" });
        await Task.WhenAll(closeTask, decisionTask);
        using HttpResponseMessage closeResponse = await closeTask;
        using HttpResponseMessage decisionResponse = await decisionTask;

        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        Assert.Contains(
            decisionResponse.StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PollStatus status = await db.Polls
            .Where(item => item.Id == pollId)
            .Select(item => item.Status)
            .SingleAsync();
        Decision? decision = await db.Decisions
            .SingleOrDefaultAsync(item => item.PollId == pollId);
        Assert.Equal(PollStatus.Closed, status);
        if (decisionResponse.StatusCode == HttpStatusCode.Conflict)
        {
            Assert.Null(decision);
        }
        else
        {
            Assert.NotNull(decision);
        }
    }

    [Fact]
    public async Task ConcurrentPuts_CreateExactlyOneDecisionWithoutServerErrors()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        Guid pollId = await SeedPollAsync(owner.Id, PollStatus.Closed);
        HttpClient[] clients = Enumerable.Range(0, 6)
            .Select(_ => _factory.CreateHttpsClient())
            .ToArray();
        try
        {
            await Task.WhenAll(clients.Select(client => LoginAsync(client, owner)));
            HttpResponseMessage[] responses = await Task.WhenAll(
                clients.Select((client, index) => client.PutAsJsonAsync(
                    $"/api/polls/{pollId}/decision",
                    new { Text = $"Concurrent version {index}" })));

            Assert.All(
                responses,
                response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            using IServiceScope scope = _factory.Services.CreateScope();
            Decision[] stored = await scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>()
                .Decisions
                .Where(item => item.PollId == pollId)
                .ToArrayAsync();
            Decision decision = Assert.Single(stored);
            Assert.Contains(
                decision.Text,
                Enumerable.Range(0, 6).Select(index => $"Concurrent version {index}"));
            Assert.Equal(owner.Id, decision.AuthorId);
        }
        finally
        {
            foreach (HttpClient client in clients)
            {
                client.Dispose();
            }
        }
    }

    private async Task<SeededUser> CreateUserAsync(string? role)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (role is not null && !await roleManager.RoleExistsAsync(role))
        {
            IdentityResult roleResult =
                await roleManager.CreateAsync(new IdentityRole<Guid>(role));
            Assert.True(roleResult.Succeeded);
        }

        string email = $"decision-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        const string password = "Decision-password-123!";
        IdentityResult userResult = await userManager.CreateAsync(user, password);
        Assert.True(userResult.Succeeded);
        if (role is not null)
        {
            IdentityResult roleAssignment = await userManager.AddToRoleAsync(user, role);
            Assert.True(roleAssignment.Succeeded);
        }

        return new SeededUser(user.Id, email, password);
    }

    private async Task<Guid> SeedPollAsync(Guid creatorId, PollStatus status)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Title = "Decision test poll",
            Status = status,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            PublishedAt = status == PollStatus.Draft ? null : DateTime.UtcNow.AddHours(-1),
            ClosedAt = status == PollStatus.Closed ? DateTime.UtcNow : null
        };
        db.Polls.Add(poll);
        await db.SaveChangesAsync();
        return poll.Id;
    }

    private async Task SeedDecisionAsync(
        Guid pollId,
        Guid authorId,
        string text,
        DateTime? publishedAt)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Decisions.Add(new Decision
        {
            Id = Guid.NewGuid(),
            PollId = pollId,
            AuthorId = authorId,
            Text = text,
            PublishedAt = publishedAt
        });
        await db.SaveChangesAsync();
    }

    private async Task SetPollStatusAsync(Guid pollId, PollStatus status)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Poll poll = await db.Polls.SingleAsync(item => item.Id == pollId);
        poll.Status = status;
        poll.ClosedAt = status == PollStatus.Closed ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
    }

    private async Task AssertDecisionCountAsync(Guid pollId, int expected)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(expected, await db.Decisions.CountAsync(item => item.PollId == pollId));
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<DecisionResponse> PutDecisionAsync(
        HttpClient client,
        Guid pollId,
        string text)
    {
        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/polls/{pollId}/decision",
            new UpsertDecisionRequest(text));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        DecisionResponse? decision =
            await response.Content.ReadFromJsonAsync<DecisionResponse>();
        return Assert.IsType<DecisionResponse>(decision);
    }

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
