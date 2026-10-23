using System.Net;
using System.Net.Http.Json;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Auth;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Tests.Auth;
using AnonPulse.Api.Tests.Voting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Admin;

public sealed class AdminApiEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_admin_tests")
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
    public async Task AdminEndpoints_RequireAuthenticationAndAdminRole()
    {
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient anonymous = _factory.CreateHttpsClient();
        using HttpClient teacherClient = _factory.CreateHttpsClient();
        await LoginAsync(teacherClient, teacher);

        HttpResponseMessage anonymousResponse =
            await anonymous.GetAsync("/api/admin/dashboard");
        HttpResponseMessage teacherResponse =
            await teacherClient.GetAsync("/api/admin/dashboard");

        AssertProblem(anonymousResponse, HttpStatusCode.Unauthorized);
        AssertProblem(teacherResponse, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Dashboard_ReturnsAggregatesWithoutPersonalData()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        await SeedPollAsync(teacher.Id, PollStatus.Open, includeResponses: true);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);

        HttpResponseMessage response = await client.GetAsync("/api/admin/dashboard");
        string body = await response.Content.ReadAsStringAsync();
        AdminDashboardResponse? dashboard =
            await response.Content.ReadFromJsonAsync<AdminDashboardResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(dashboard);
        Assert.True(dashboard.Users.Total >= 2);
        Assert.Contains(dashboard.Users.ByRole, item =>
            item.Role == RoleNames.Admin && item.Count >= 1);
        Assert.Contains(dashboard.Users.ByRole, item =>
            item.Role == RoleNames.Teacher && item.Count >= 1);
        Assert.True(dashboard.Polls.Open >= 1);
        Assert.True(dashboard.TotalParticipations >= 1);
        Assert.Equal(30, dashboard.PollCreationLast30Days.Count);
        Assert.DoesNotContain("@example.test", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserListing_SearchRoleStateAndDetailsArePaged()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        await SeedPollAsync(teacher.Id, PollStatus.Draft, includeResponses: false);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);

        PagedResponse<AdminUserListItemResponse>? page = await client.GetFromJsonAsync<
            PagedResponse<AdminUserListItemResponse>>(
            $"/api/admin/users?search={Uri.EscapeDataString(teacher.Email)}" +
            "&role=teacher&state=active&page=1&pageSize=10");
        AdminUserDetailsResponse? details = await client.GetFromJsonAsync<
            AdminUserDetailsResponse>($"/api/admin/users/{teacher.Id}");

        Assert.NotNull(page);
        AdminUserListItemResponse item = Assert.Single(page.Items);
        Assert.Equal(teacher.Id, item.Id);
        Assert.Equal([RoleNames.Teacher], item.Roles);
        Assert.NotNull(details);
        Assert.Equal(teacher.Id, details.Id);
        Assert.Equal(1, details.CreatedPollCount);
    }

    [Fact]
    public async Task Deactivation_RequiresCsrfInvalidatesCookieAndBlocksNewLogin()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient adminClient = _factory.CreateHttpsClient();
        using HttpClient teacherClient = _factory.CreateHttpsClient();
        await LoginAsync(adminClient, admin);
        await LoginAsync(teacherClient, teacher);

        HttpResponseMessage missingCsrf = await adminClient.PatchAsJsonAsync(
            $"/api/admin/users/{teacher.Id}/status",
            new { IsDeactivated = true });
        string csrf = await GetCsrfTokenAsync(adminClient);
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/admin/users/{teacher.Id}/status")
        {
            Content = JsonContent.Create(new { IsDeactivated = true })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        HttpResponseMessage blocked = await adminClient.SendAsync(request);
        HttpResponseMessage existingSession =
            await teacherClient.GetAsync("/api/auth/me");
        using HttpClient newClient = _factory.CreateHttpsClient();
        HttpResponseMessage newLogin = await newClient.PostAsJsonAsync(
            "/api/auth/login",
            new { teacher.Email, teacher.Password });

        AssertProblem(missingCsrf, HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        AssertProblem(existingSession, HttpStatusCode.Unauthorized);
        AssertProblem(newLogin, HttpStatusCode.Unauthorized);

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True((await db.Users.SingleAsync(user => user.Id == teacher.Id))
            .IsAdministrativelyDeactivated);
        Assert.Single(db.AdminAuditEvents, item =>
            item.TargetId == teacher.Id &&
            item.ActionType == AdminAuditActionTypes.UserDeactivated);
    }

    [Fact]
    public async Task AdministratorCannotDeactivateOrDeleteSelf()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);
        string csrf = await GetCsrfTokenAsync(client);

        HttpResponseMessage deactivate = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"/api/admin/users/{admin.Id}/status",
            new { IsDeactivated = true },
            csrf);
        HttpResponseMessage delete = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/admin/users/{admin.Id}",
            csrf);

        await AssertConflictCodeAsync(deactivate, "self_deactivation_forbidden");
        await AssertConflictCodeAsync(delete, "self_deletion_forbidden");
    }

    [Fact]
    public async Task UserWithBusinessDependenciesCannotBeDeleted()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        Poll poll = await SeedPollAsync(
            teacher.Id,
            PollStatus.Closed,
            includeResponses: false,
            decisionAuthorId: teacher.Id);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);
        string csrf = await GetCsrfTokenAsync(client);

        HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/admin/users/{teacher.Id}",
            csrf);

        await AssertConflictCodeAsync(response, "user_has_dependencies");
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.Users.AnyAsync(user => user.Id == teacher.Id));
        Assert.True(await db.Polls.AnyAsync(item => item.Id == poll.Id));
    }

    [Fact]
    public async Task CleanUserDeletionAndAuditAreAtomic()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser target = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);
        string csrf = await GetCsrfTokenAsync(client);

        HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/admin/users/{target.Id}",
            csrf);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Users.AnyAsync(user => user.Id == target.Id));
        Assert.Single(db.AdminAuditEvents, item =>
            item.TargetId == target.Id &&
            item.ActionType == AdminAuditActionTypes.UserDeleted);
    }

    [Fact]
    public async Task AdminPollDeletionRequiresConfirmationAndRemovesWholeAggregate()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        Poll poll = await SeedPollAsync(
            teacher.Id,
            PollStatus.Closed,
            includeResponses: true,
            decisionAuthorId: teacher.Id);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);
        string csrf = await GetCsrfTokenAsync(client);

        HttpResponseMessage withoutConfirmation = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/admin/polls/{poll.Id}",
            csrf);
        HttpResponseMessage confirmed = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/admin/polls/{poll.Id}?confirm=true",
            csrf);

        await AssertConflictCodeAsync(
            withoutConfirmation,
            "poll_deletion_confirmation_required");
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Polls.AnyAsync(item => item.Id == poll.Id));
        Assert.False(await db.PollQuestions.AnyAsync(item => item.PollId == poll.Id));
        Assert.False(await db.PollOptions.AnyAsync(item => item.Question.PollId == poll.Id));
        Assert.False(await db.Votes.AnyAsync(item => item.Option.Question.PollId == poll.Id));
        Assert.False(await db.VoteParticipations.AnyAsync(item => item.PollId == poll.Id));
        Assert.False(await db.Decisions.AnyAsync(item => item.PollId == poll.Id));
        AdminAuditEvent audit = Assert.Single(db.AdminAuditEvents, item =>
            item.TargetId == poll.Id &&
            item.ActionType == AdminAuditActionTypes.PollDeleted);
        Assert.DoesNotContain("token", audit.MetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answer", audit.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminPollListingAndDetailsSupportFiltersAndAllOwners()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser firstTeacher = await CreateUserAsync(RoleNames.Teacher);
        SeededUser secondTeacher = await CreateUserAsync(RoleNames.Teacher);
        Poll firstPoll = await SeedPollAsync(
            firstTeacher.Id,
            PollStatus.Open,
            includeResponses: true);
        await SeedPollAsync(secondTeacher.Id, PollStatus.Closed, false);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);

        HttpResponseMessage listResponse = await client.GetAsync(
            $"/api/admin/polls?creatorId={firstTeacher.Id}&status=open" +
            $"&search={Uri.EscapeDataString(firstPoll.Title!)}&page=1&pageSize=10");
        PagedResponse<AdminPollListItemResponse>? page =
            await listResponse.Content.ReadFromJsonAsync<
                PagedResponse<AdminPollListItemResponse>>();
        AdminPollDetailsResponse? details = await client.GetFromJsonAsync<
            AdminPollDetailsResponse>($"/api/admin/polls/{firstPoll.Id}");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.NotNull(page);
        AdminPollListItemResponse item = Assert.Single(page.Items);
        Assert.Equal(firstPoll.Id, item.Id);
        Assert.Equal("open", item.Status);
        Assert.Equal(1, item.QuestionCount);
        Assert.Equal(1, item.ParticipationCount);
        Assert.NotNull(details);
        Assert.Equal(firstPoll.Id, details.Id);
        Assert.Equal(firstTeacher.Id, details.CreatorId);
        Assert.Equal(1, details.VoteCount);
        Assert.Single(details.Questions);
    }

    [Fact]
    public async Task ConcurrentAdminDeletionAndCloseNeverLeavePartialPollData()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser teacher = await CreateUserAsync(RoleNames.Teacher);
        Poll poll = await SeedPollAsync(
            teacher.Id,
            PollStatus.Open,
            includeResponses: true,
            decisionAuthorId: teacher.Id);
        using HttpClient closeClient = _factory.CreateHttpsClient();
        using HttpClient deleteClient = _factory.CreateHttpsClient();
        await LoginAsync(closeClient, admin);
        await LoginAsync(deleteClient, admin);
        string csrf = await GetCsrfTokenAsync(deleteClient);

        Task<HttpResponseMessage> closeTask = closeClient.PostAsync(
            $"/api/polls/{poll.Id}/close",
            content: null);
        Task<HttpResponseMessage> deleteTask = SendAsync(
            deleteClient,
            HttpMethod.Delete,
            $"/api/admin/polls/{poll.Id}?confirm=true",
            csrf);
        HttpResponseMessage[] responses = await Task.WhenAll(closeTask, deleteTask);

        Assert.Equal(HttpStatusCode.NoContent, responses[1].StatusCode);
        Assert.Contains(
            responses[0].StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.NotFound });
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Polls.AnyAsync(item => item.Id == poll.Id));
        Assert.False(await db.PollQuestions.AnyAsync(item => item.PollId == poll.Id));
        Assert.False(await db.PollOptions.AnyAsync(item => item.Question.PollId == poll.Id));
        Assert.False(await db.Votes.AnyAsync(item => item.Option.Question.PollId == poll.Id));
        Assert.False(await db.VoteParticipations.AnyAsync(item => item.PollId == poll.Id));
        Assert.False(await db.Decisions.AnyAsync(item => item.PollId == poll.Id));
    }

    [Fact]
    public async Task TeacherCanDeleteOnlyOwnDraft()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser foreign = await CreateUserAsync(RoleNames.Teacher);
        Poll draft = await SeedPollAsync(owner.Id, PollStatus.Draft, false);
        Poll open = await SeedPollAsync(owner.Id, PollStatus.Open, false);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient foreignClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(foreignClient, foreign);
        string ownerCsrf = await GetCsrfTokenAsync(ownerClient);
        string foreignCsrf = await GetCsrfTokenAsync(foreignClient);

        HttpResponseMessage foreignDelete = await SendAsync(
            foreignClient,
            HttpMethod.Delete,
            $"/api/polls/{draft.Id}",
            foreignCsrf);
        HttpResponseMessage openDelete = await SendAsync(
            ownerClient,
            HttpMethod.Delete,
            $"/api/polls/{open.Id}",
            ownerCsrf);
        HttpResponseMessage draftDelete = await SendAsync(
            ownerClient,
            HttpMethod.Delete,
            $"/api/polls/{draft.Id}",
            ownerCsrf);

        AssertProblem(foreignDelete, HttpStatusCode.NotFound);
        await AssertConflictCodeAsync(openDelete, "only_draft_poll_can_be_deleted");
        Assert.Equal(HttpStatusCode.NoContent, draftDelete.StatusCode);
    }

    [Fact]
    public async Task AuditEndpointSupportsFilteringAndNeverReturnsSecrets()
    {
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser target = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, admin);
        string csrf = await GetCsrfTokenAsync(client);
        await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"/api/admin/users/{target.Id}/status",
            new { IsDeactivated = true },
            csrf);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/admin/audit?actionType={AdminAuditActionTypes.UserDeactivated}" +
            $"&targetType=user&targetId={target.Id}");
        string body = await response.Content.ReadAsStringAsync();
        PagedResponse<AdminAuditResponse>? page =
            await response.Content.ReadFromJsonAsync<PagedResponse<AdminAuditResponse>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(page);
        AdminAuditResponse item = Assert.Single(page.Items);
        Assert.Equal(target.Id, item.TargetId);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SeededUser> CreateUserAsync(params string[] roles)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (string role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                IdentityResult roleResult =
                    await roleManager.CreateAsync(new IdentityRole<Guid>(role));
                Assert.True(roleResult.Succeeded);
            }
        }

        string email = $"admin-tests-{Guid.NewGuid():N}@example.test";
        const string password = "Admin-tests-password-123!";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        IdentityResult creation = await userManager.CreateAsync(user, password);
        Assert.True(creation.Succeeded);
        foreach (string role in roles)
        {
            IdentityResult assignment = await userManager.AddToRoleAsync(user, role);
            Assert.True(assignment.Succeeded);
        }

        return new SeededUser(user.Id, email, password);
    }

    private async Task<Poll> SeedPollAsync(
        Guid creatorId,
        PollStatus status,
        bool includeResponses,
        Guid? decisionAuthorId = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Title = $"Admin test {Guid.NewGuid():N}",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            PublishedAt = status == PollStatus.Draft ? null : DateTime.UtcNow,
            ClosedAt = status == PollStatus.Closed ? DateTime.UtcNow : null
        };
        var question = new PollQuestion
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Text = "Administrative test question",
            Position = 0
        };
        var first = new PollOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Text = "First",
            Position = 0
        };
        question.Options.Add(first);
        question.Options.Add(new PollOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Text = "Second",
            Position = 1
        });
        poll.Questions.Add(question);
        if (includeResponses)
        {
            db.Votes.Add(new Vote
            {
                Id = Guid.NewGuid(),
                OptionId = first.Id,
                CreatedAt = null
            });
            poll.VoteParticipations.Add(new VoteParticipation
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                TokenHash = new string('a', 64),
                UsedAt = DateTime.UtcNow
            });
        }

        if (decisionAuthorId is not null)
        {
            poll.Decision = new Decision
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                AuthorId = decisionAuthorId.Value,
                Text = "Administrative test decision",
                PublishedAt = DateTime.UtcNow
            };
        }

        db.Polls.Add(poll);
        await db.SaveChangesAsync();
        return poll;
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        CsrfTokenResponse? response = await client.GetFromJsonAsync<CsrfTokenResponse>(
            "/api/auth/csrf-token");
        return Assert.IsType<CsrfTokenResponse>(response).Token;
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string uri,
        string csrfToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string uri,
        T body,
        string csrfToken)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return client.SendAsync(request);
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

    private static async Task AssertConflictCodeAsync(
        HttpResponseMessage response,
        string expectedCode)
    {
        AssertProblem(response, HttpStatusCode.Conflict);
        Dictionary<string, object>? problem =
            await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(problem);
        Assert.Equal(expectedCode, problem["code"].ToString());
    }
}
