using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using AnonPulse.Api.Contracts.PollResults;
using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.PollResults;
using AnonPulse.Api.Tests.Auth;
using AnonPulse.Api.Tests.Voting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.PollResults;

public sealed class PollResultsEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_results_tests")
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
    public async Task OpenMultiQuestionPoll_OwnerReceivesOrderedInterimResults()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Open,
            [[2, 1], [1, 0, 2]],
            participants: 3,
            creatorId: owner.Id);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, owner);

        HttpResponseMessage response =
            await client.GetAsync($"/api/polls/{poll.Id}/results");
        string json = await response.Content.ReadAsStringAsync();
        PollResultsResponse? results =
            await response.Content.ReadFromJsonAsync<PollResultsResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(results);
        Assert.Equal(poll.Id, results.Id);
        Assert.Equal("open", results.Status);
        Assert.Equal("interim", results.ResultsType);
        Assert.Equal(3, results.TotalParticipants);
        Assert.Equal([0, 1], results.Questions.Select(question => question.Position));
        Assert.Equal(
            [0, 1],
            results.Questions[0].Options.Select(option => option.Position));
        Assert.Equal(
            [0, 1, 2],
            results.Questions[1].Options.Select(option => option.Position));
        Assert.Equal(
            [2, 1],
            results.Questions[0].Options.Select(option => option.VoteCount));
        Assert.Equal(
            [66.67m, 33.33m],
            results.Questions[0].Options.Select(option => option.Percentage));
        Assert.Equal(
            [33.33m, 0m, 66.67m],
            results.Questions[1].Options.Select(option => option.Percentage));
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creatorId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdAt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClosedPoll_ReturnsFinalResults()
    {
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Closed,
            [[0, 1]],
            participants: 1);
        using HttpClient client = _factory.CreateHttpsClient();

        PollResultsResponse? results = await client.GetFromJsonAsync<PollResultsResponse>(
            $"/api/polls/{poll.Id}/results");

        Assert.NotNull(results);
        Assert.Equal("closed", results.Status);
        Assert.Equal("final", results.ResultsType);
        Assert.Equal(1, results.TotalParticipants);
        Assert.Equal([0m, 100m], results.Questions[0].Options.Select(x => x.Percentage));
    }

    [Fact]
    public async Task OpenPoll_EnforcesOwnerAndRoleAccess()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser admin = await CreateUserAsync(RoleNames.Admin);
        SeededUser foreignTeacher = await CreateUserAsync(RoleNames.Teacher);
        SeededUser wrongRole = await CreateUserAsync(role: null);
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Open,
            [[1, 0]],
            participants: 1,
            creatorId: owner.Id);
        using HttpClient anonymousClient = _factory.CreateHttpsClient();
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient adminClient = _factory.CreateHttpsClient();
        using HttpClient foreignClient = _factory.CreateHttpsClient();
        using HttpClient wrongRoleClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(adminClient, admin);
        await LoginAsync(foreignClient, foreignTeacher);
        await LoginAsync(wrongRoleClient, wrongRole);

        HttpResponseMessage anonymous =
            await anonymousClient.GetAsync($"/api/polls/{poll.Id}/results");
        HttpResponseMessage ownerResponse =
            await ownerClient.GetAsync($"/api/polls/{poll.Id}/results");
        HttpResponseMessage adminResponse =
            await adminClient.GetAsync($"/api/polls/{poll.Id}/results");
        HttpResponseMessage foreign =
            await foreignClient.GetAsync($"/api/polls/{poll.Id}/results");
        HttpResponseMessage forbidden =
            await wrongRoleClient.GetAsync($"/api/polls/{poll.Id}/results");

        AssertProblem(anonymous, HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        AssertProblem(foreign, HttpStatusCode.NotFound);
        AssertProblem(forbidden, HttpStatusCode.Forbidden);
        PollResultsResponse? ownerResults =
            await ownerResponse.Content.ReadFromJsonAsync<PollResultsResponse>();
        PollResultsResponse? adminResults =
            await adminResponse.Content.ReadFromJsonAsync<PollResultsResponse>();
        Assert.Equal("interim", ownerResults?.ResultsType);
        Assert.Equal("interim", adminResults?.ResultsType);
    }

    [Fact]
    public async Task OpenPollWithoutVotes_OwnerReceivesZeros()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Open,
            [[0, 0], [0, 0]],
            participants: 0,
            creatorId: owner.Id);
        using HttpClient client = _factory.CreateHttpsClient();
        await LoginAsync(client, owner);

        PollResultsResponse? results = await client.GetFromJsonAsync<PollResultsResponse>(
            $"/api/polls/{poll.Id}/results");

        Assert.NotNull(results);
        Assert.Equal(0, results.TotalParticipants);
        Assert.All(
            results.Questions.SelectMany(question => question.Options),
            option =>
            {
                Assert.Equal(0, option.VoteCount);
                Assert.Equal(0m, option.Percentage);
            });
    }

    [Fact]
    public async Task DraftAndUnknownPolls_ReturnProblemDetailsWithoutDraftContent()
    {
        SeededResultsPoll draft = await SeedPollAsync(
            PollStatus.Draft,
            [[0, 0]],
            participants: 0,
            title: "SECRET DRAFT TITLE");
        using HttpClient client = _factory.CreateHttpsClient();

        HttpResponseMessage draftResponse =
            await client.GetAsync($"/api/polls/{draft.Id}/results");
        string draftJson = await draftResponse.Content.ReadAsStringAsync();
        HttpResponseMessage missingResponse =
            await client.GetAsync($"/api/polls/{Guid.NewGuid()}/results");
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        using HttpClient authenticatedClient = _factory.CreateHttpsClient();
        await LoginAsync(authenticatedClient, owner);
        HttpResponseMessage authenticatedDraftResponse =
            await authenticatedClient.GetAsync($"/api/polls/{draft.Id}/results");

        Assert.Equal(HttpStatusCode.Conflict, draftResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            draftResponse.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("SECRET DRAFT TITLE", draftJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Question 0", draftJson, StringComparison.Ordinal);
        AssertProblem(authenticatedDraftResponse, HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            missingResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnonymousVote_RemainsFunctionalAndAppearsInResults()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Open,
            [[0, 0]],
            participants: 0,
            creatorId: owner.Id);
        using HttpClient client = _factory.CreateHttpsClient();
        await client.GetAsync($"/api/public/polls/{poll.Id}");
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/public/polls/{poll.Id}/votes");
        request.Headers.Add("X-AnonPulse-Vote", "1");
        request.Content = JsonContent.Create(new SubmitVotesRequest(
        [
            new VoteAnswerRequest(poll.QuestionIds[0], poll.OptionIds[0][1])
        ]));

        HttpResponseMessage voteResponse = await client.SendAsync(request);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        PollResultsResponse? results = await ownerClient.GetFromJsonAsync<PollResultsResponse>(
            $"/api/polls/{poll.Id}/results");

        Assert.Equal(HttpStatusCode.NoContent, voteResponse.StatusCode);
        Assert.NotNull(results);
        Assert.Equal(1, results.TotalParticipants);
        Assert.Equal([0, 1], results.Questions[0].Options.Select(x => x.VoteCount));
        Assert.Equal([0m, 100m], results.Questions[0].Options.Select(x => x.Percentage));
    }

    [Fact]
    public async Task ConcurrentVote_DoesNotProduceMixedSnapshot()
    {
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Open,
            [[0, 0]],
            participants: 0);
        var interceptor = new SnapshotBarrierInterceptor();
        var readerOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .AddInterceptors(interceptor)
            .Options;
        var writerOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using var readerContext = new ApplicationDbContext(readerOptions);
        await using var writerContext = new ApplicationDbContext(writerOptions);
        var service = new PollResultsService(readerContext);

        Task<PollResultsResult> readTask = service.GetAsync(
            poll.Id,
            new PollResultsAccessContext(
                poll.CreatorId,
                IsAuthenticated: true,
                IsTeacher: true,
                IsAdmin: false));
        await interceptor.SnapshotEstablished.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            writerContext.Votes.Add(new Vote
            {
                Id = Guid.NewGuid(),
                OptionId = poll.OptionIds[0][0],
                CreatedAt = null
            });
            writerContext.VoteParticipations.Add(new VoteParticipation
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                TokenHash = new string('f', 64),
                UsedAt = DateTime.UtcNow
            });
            Poll pollToClose = await writerContext.Polls.SingleAsync(
                item => item.Id == poll.Id);
            pollToClose.Status = PollStatus.Closed;
            pollToClose.ClosedAt = DateTime.UtcNow;
            await writerContext.SaveChangesAsync();
        }
        finally
        {
            interceptor.Release();
        }

        PollResultsResult snapshot = await readTask;

        Assert.NotNull(snapshot.Results);
        Assert.Equal("interim", snapshot.Results.ResultsType);
        Assert.Equal(0, snapshot.Results.TotalParticipants);
        Assert.All(
            snapshot.Results.Questions.SelectMany(question => question.Options),
            option => Assert.Equal(0, option.VoteCount));
        Assert.Equal(1, await writerContext.Votes.CountAsync(
            vote => vote.Option.Question.PollId == poll.Id));
        Assert.Equal(1, await writerContext.VoteParticipations.CountAsync(
            participation => participation.PollId == poll.Id));
        Assert.Equal(PollStatus.Closed, await writerContext.Polls
            .Where(item => item.Id == poll.Id)
            .Select(item => item.Status)
            .SingleAsync());
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

        string email = $"results-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        const string password = "Results-password-123!";
        IdentityResult userResult = await userManager.CreateAsync(user, password);
        Assert.True(userResult.Succeeded);
        if (role is not null)
        {
            IdentityResult roleAssignment = await userManager.AddToRoleAsync(user, role);
            Assert.True(roleAssignment.Succeeded);
        }

        return new SeededUser(user.Id, email, password);
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    private async Task<SeededResultsPoll> SeedPollAsync(
        PollStatus status,
        IReadOnlyList<IReadOnlyList<int>> voteCounts,
        int participants,
        string? title = "Results test",
        Guid? creatorId = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser? teacher = creatorId is null ? new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"results-{Guid.NewGuid():N}@example.test",
            Email = $"results-{Guid.NewGuid():N}@example.test"
        } : null;
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId ?? teacher!.Id,
            Title = title,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            PublishedAt = status == PollStatus.Draft ? null : DateTime.UtcNow,
            ClosedAt = status == PollStatus.Closed ? DateTime.UtcNow : null
        };
        var questionIds = new List<Guid>();
        var optionIds = new List<IReadOnlyList<Guid>>();

        for (int questionIndex = voteCounts.Count - 1; questionIndex >= 0; questionIndex--)
        {
            var question = new PollQuestion
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                Text = $"Question {questionIndex}",
                Position = questionIndex
            };
            var currentOptionIds = new List<Guid>();
            for (int optionIndex = voteCounts[questionIndex].Count - 1;
                 optionIndex >= 0;
                 optionIndex--)
            {
                var option = new PollOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = question.Id,
                    Text = $"Option {questionIndex}-{optionIndex}",
                    Position = optionIndex
                };
                for (int voteIndex = 0;
                     voteIndex < voteCounts[questionIndex][optionIndex];
                     voteIndex++)
                {
                    option.Votes.Add(new Vote
                    {
                        Id = Guid.NewGuid(),
                        OptionId = option.Id,
                        CreatedAt = null
                    });
                }

                question.Options.Add(option);
            }

            poll.Questions.Add(question);
        }

        PollQuestion[] orderedQuestions = poll.Questions
            .OrderBy(question => question.Position)
            .ToArray();
        foreach (PollQuestion question in orderedQuestions)
        {
            questionIds.Add(question.Id);
            optionIds.Add(question.Options
                .OrderBy(option => option.Position)
                .Select(option => option.Id)
                .ToArray());
        }

        for (int index = 0; index < participants; index++)
        {
            poll.VoteParticipations.Add(new VoteParticipation
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                TokenHash = index.ToString("x64"),
                UsedAt = DateTime.UtcNow
            });
        }

        if (teacher is not null)
        {
            db.Users.Add(teacher);
        }
        db.Polls.Add(poll);
        await db.SaveChangesAsync();
        return new SeededResultsPoll(poll.Id, poll.CreatorId, questionIds, optionIds);
    }

    private sealed record SeededResultsPoll(
        Guid Id,
        Guid CreatorId,
        IReadOnlyList<Guid> QuestionIds,
        IReadOnlyList<IReadOnlyList<Guid>> OptionIds);

    private sealed class SnapshotBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _snapshotEstablished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hasPaused;

        public Task SnapshotEstablished => _snapshotEstablished.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM polls", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _hasPaused, 1) == 0)
            {
                _snapshotEstablished.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
