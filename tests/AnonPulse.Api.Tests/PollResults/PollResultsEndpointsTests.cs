using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using AnonPulse.Api.Contracts.PollResults;
using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.PollResults;
using AnonPulse.Api.Tests.Voting;
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
    public async Task OpenMultiQuestionPoll_ReturnsOrderedPublicInterimResults()
    {
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Open,
            [[2, 1], [1, 0, 2]],
            participants: 3);
        using HttpClient client = _factory.CreateHttpsClient();

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
    public async Task PublishedPollWithoutVotes_ReturnsZeros()
    {
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Open,
            [[0, 0], [0, 0]],
            participants: 0);
        using HttpClient client = _factory.CreateHttpsClient();

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

        Assert.Equal(HttpStatusCode.Conflict, draftResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            draftResponse.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("SECRET DRAFT TITLE", draftJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Question 0", draftJson, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            missingResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnonymousVote_RemainsFunctionalAndAppearsInResults()
    {
        SeededResultsPoll poll = await SeedPollAsync(
            PollStatus.Open,
            [[0, 0]],
            participants: 0);
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
        PollResultsResponse? results = await client.GetFromJsonAsync<PollResultsResponse>(
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

        Task<PollResultsResult> readTask = service.GetAsync(poll.Id);
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
            await writerContext.SaveChangesAsync();
        }
        finally
        {
            interceptor.Release();
        }

        PollResultsResult snapshot = await readTask;

        Assert.NotNull(snapshot.Results);
        Assert.Equal(0, snapshot.Results.TotalParticipants);
        Assert.All(
            snapshot.Results.Questions.SelectMany(question => question.Options),
            option => Assert.Equal(0, option.VoteCount));
        Assert.Equal(1, await writerContext.Votes.CountAsync(
            vote => vote.Option.Question.PollId == poll.Id));
        Assert.Equal(1, await writerContext.VoteParticipations.CountAsync(
            participation => participation.PollId == poll.Id));
    }

    private async Task<SeededResultsPoll> SeedPollAsync(
        PollStatus status,
        IReadOnlyList<IReadOnlyList<int>> voteCounts,
        int participants,
        string? title = "Results test")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var teacher = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"results-{Guid.NewGuid():N}@example.test",
            Email = $"results-{Guid.NewGuid():N}@example.test"
        };
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = teacher.Id,
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

        db.Users.Add(teacher);
        db.Polls.Add(poll);
        await db.SaveChangesAsync();
        return new SeededResultsPoll(poll.Id, questionIds, optionIds);
    }

    private sealed record SeededResultsPoll(
        Guid Id,
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
