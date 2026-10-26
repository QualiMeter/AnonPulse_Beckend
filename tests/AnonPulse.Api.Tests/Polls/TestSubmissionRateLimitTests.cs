using System.Net;
using System.Net.Http.Json;
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

public sealed class TestSubmissionRateLimitTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_test_submission_rate_limit")
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
                ["TestSubmissions:RateLimitWindow"] = "00:10:00",
                ["TestSubmissions:RequestsPerClient"] = "3"
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
    public async Task TestPolicy_LimitsPeerAddress_IgnoresForwardedFor_AndKeepsIdempotency()
    {
        SeededUser teacher = await CreateTeacherAsync();
        using HttpClient teacherClient = _factory.CreateHttpsClient();
        await LoginAsync(teacherClient, teacher);
        PollResponse test = await CreateAndPublishTestAsync(teacherClient);

        using HttpClient participant = _factory.CreateHttpsClient();
        PublicPollResponse publicPoll = (await participant.GetFromJsonAsync<PublicPollResponse>(
            $"/api/public/polls/{test.Id}"))!;
        Guid firstRequestId = Guid.NewGuid();
        object firstBody = Submission(publicPoll, firstRequestId);

        HttpResponseMessage first = await SubmitAsync(participant, test.Id, firstBody);
        HttpResponseMessage replay = await SubmitAsync(participant, test.Id, firstBody);
        HttpResponseMessage second = await SubmitAsync(
            participant, test.Id, Submission(publicPoll, Guid.NewGuid()));
        HttpResponseMessage limited = await SubmitAsync(
            participant,
            test.Id,
            Submission(publicPoll, Guid.NewGuid()),
            forwardedFor: "203.0.113.99");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Contains("secret_unavailable", await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, await db.NamedAttempts.CountAsync(item => item.PollId == test.Id));
        Assert.Equal(1, await db.NamedAttempts.CountAsync(item =>
            item.PollId == test.Id && item.ClientRequestId == firstRequestId));
    }

    private async Task<SeededUser> CreateTeacherAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roles = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roles.RoleExistsAsync(RoleNames.Teacher))
        {
            Assert.True((await roles.CreateAsync(
                new IdentityRole<Guid>(RoleNames.Teacher))).Succeeded);
        }
        string email = $"test-rate-limit-{Guid.NewGuid():N}@example.test";
        const string password = "Test-rate-limit-password-123!";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, RoleNames.Teacher)).Succeeded);
        return new SeededUser(user.Id, email, password);
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/auth/login", new { user.Email, user.Password })).StatusCode);

    private static async Task<PollResponse> CreateAndPublishTestAsync(HttpClient client)
    {
        HttpResponseMessage create = await client.PostAsJsonAsync("/api/polls", new
        {
            Title = "Rate-limited test",
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
                    Weight = 1,
                    CorrectOptionIndexes = new[] { 0 }
                }
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        PollResponse poll = (await create.Content.ReadFromJsonAsync<PollResponse>())!;
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/polls/{poll.Id}/publish", null)).StatusCode);
        return poll;
    }

    private static object Submission(PublicPollResponse poll, Guid clientRequestId) => new
    {
        RespondentName = "Student",
        RespondentGroup = "Group",
        ClientRequestId = clientRequestId,
        Answers = new[]
        {
            new
            {
                QuestionId = poll.Questions[0].Id,
                OptionId = poll.Questions[0].Options[0].Id
            }
        }
    };

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        Guid pollId,
        object body,
        string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/public/polls/{pollId}/test-submissions")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-AnonPulse-Test", "1");
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }
        return await client.SendAsync(request);
    }
}
