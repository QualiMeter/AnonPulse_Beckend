using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Voting;

public sealed class PublicVotingEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_voting_tests")
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
    public async Task AnonymousParticipant_CanReadAndVoteOnSingleQuestionPoll()
    {
        SeededPoll poll = await SeedPollAsync(PollStatus.Open, [2]);
        using HttpClient client = _factory.CreateHttpsClient();

        HttpResponseMessage getResponse = await client.GetAsync($"/api/public/polls/{poll.Id}");
        PublicPollResponse? publicPoll =
            await getResponse.Content.ReadFromJsonAsync<PublicPollResponse>();
        HttpResponseMessage voteResponse = await VoteAsync(
            client,
            poll.Id,
            [new(poll.QuestionIds[0], poll.OptionIds[0][0])]);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.NotNull(publicPoll);
        Assert.Equal(poll.Id, publicPoll.Id);
        Assert.Single(publicPoll.Questions);
        Assert.Equal([0, 1], publicPoll.Questions[0].Options.Select(item => item.Position));
        Assert.Equal(HttpStatusCode.NoContent, voteResponse.StatusCode);
        await AssertStoredCountsAsync(poll.Id, votes: 1, participations: 1);
    }

    [Fact]
    public async Task PublicGet_ForUnknownPoll_Returns404AndNoStore()
    {
        using HttpClient client = _factory.CreateHttpsClient();

        HttpResponseMessage response =
            await client.GetAsync($"/api/public/polls/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task AnonymousParticipant_CanVoteOnEveryQuestionAtomically()
    {
        SeededPoll poll = await SeedPollAsync(PollStatus.Open, [2, 3]);
        using HttpClient client = _factory.CreateHttpsClient();
        await client.GetAsync($"/api/public/polls/{poll.Id}");

        HttpResponseMessage response = await VoteAsync(
            client,
            poll.Id,
            [
                new(poll.QuestionIds[0], poll.OptionIds[0][1]),
                new(poll.QuestionIds[1], poll.OptionIds[1][2])
            ]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertStoredCountsAsync(poll.Id, votes: 2, participations: 1);
    }

    [Fact]
    public async Task InvalidAnswerSets_Return400AndPersistNothing()
    {
        SeededPoll poll = await SeedPollAsync(PollStatus.Open, [2, 2]);
        SeededPoll otherPoll = await SeedPollAsync(PollStatus.Open, [2]);
        IReadOnlyList<IReadOnlyList<VoteAnswerRequest>> invalidRequests =
        [
            [],
            [new(poll.QuestionIds[0], poll.OptionIds[0][0])],
            [
                new(poll.QuestionIds[0], poll.OptionIds[0][0]),
                new(poll.QuestionIds[0], poll.OptionIds[0][1])
            ],
            [
                new(Guid.NewGuid(), poll.OptionIds[0][0]),
                new(poll.QuestionIds[1], poll.OptionIds[1][0])
            ],
            [
                new(poll.QuestionIds[0], Guid.NewGuid()),
                new(poll.QuestionIds[1], poll.OptionIds[1][0])
            ],
            [
                new(poll.QuestionIds[0], poll.OptionIds[1][0]),
                new(poll.QuestionIds[1], poll.OptionIds[1][1])
            ],
            [
                new(poll.QuestionIds[0], otherPoll.OptionIds[0][0]),
                new(poll.QuestionIds[1], poll.OptionIds[1][0])
            ]
        ];

        foreach (IReadOnlyList<VoteAnswerRequest> answers in invalidRequests)
        {
            using HttpClient client = _factory.CreateHttpsClient();
            await client.GetAsync($"/api/public/polls/{poll.Id}");
            HttpResponseMessage response = await VoteAsync(client, poll.Id, answers);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
        }

        await AssertStoredCountsAsync(poll.Id, votes: 0, participations: 0);
    }

    [Theory]
    [InlineData(PollStatus.Draft)]
    [InlineData(PollStatus.Closed)]
    public async Task PollThatIsNotOpen_CannotBeReadOrVoted(PollStatus status)
    {
        SeededPoll poll = await SeedPollAsync(status, [2]);
        SeededPoll sessionPoll = await SeedPollAsync(PollStatus.Open, [2]);
        using HttpClient client = _factory.CreateHttpsClient();
        await client.GetAsync($"/api/public/polls/{sessionPoll.Id}");

        HttpResponseMessage getResponse = await client.GetAsync($"/api/public/polls/{poll.Id}");
        HttpResponseMessage voteResponse = await VoteAsync(
            client,
            poll.Id,
            [new(poll.QuestionIds[0], poll.OptionIds[0][0])]);

        Assert.Equal(HttpStatusCode.Conflict, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, voteResponse.StatusCode);
        await AssertStoredCountsAsync(poll.Id, 0, 0);
    }

    [Fact]
    public async Task ExistingCookie_IsNotReissuedAndDuplicateVoteReturns409()
    {
        SeededPoll poll = await SeedPollAsync(PollStatus.Open, [2]);
        using HttpClient client = _factory.CreateHttpsClient();

        HttpResponseMessage firstGet = await client.GetAsync($"/api/public/polls/{poll.Id}");
        HttpResponseMessage secondGet = await client.GetAsync($"/api/public/polls/{poll.Id}");
        HttpResponseMessage firstVote = await VoteAsync(
            client, poll.Id, [new(poll.QuestionIds[0], poll.OptionIds[0][0])]);
        HttpResponseMessage secondVote = await VoteAsync(
            client, poll.Id, [new(poll.QuestionIds[0], poll.OptionIds[0][1])]);

        string setCookie = Assert.Single(firstGet.Headers.GetValues("Set-Cookie"));
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("__Host-", setCookie, StringComparison.Ordinal);
        Assert.False(secondGet.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.NoContent, firstVote.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondVote.StatusCode);
        await AssertStoredCountsAsync(poll.Id, 1, 1);
    }

    [Fact]
    public async Task ConcurrentVotesWithSameCookie_ExactlyOneSucceeds()
    {
        SeededPoll poll = await SeedPollAsync(PollStatus.Open, [2]);
        using HttpClient client = _factory.CreateHttpsClient();
        await client.GetAsync($"/api/public/polls/{poll.Id}");
        VoteAnswerRequest[] answers = [new(poll.QuestionIds[0], poll.OptionIds[0][0])];

        HttpResponseMessage[] responses = await Task.WhenAll(
            VoteAsync(client, poll.Id, answers),
            VoteAsync(client, poll.Id, answers));

        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict);
        await AssertStoredCountsAsync(poll.Id, 1, 1);
    }

    [Fact]
    public async Task SameCookie_CanVoteInTwoPollsAndHashesArePollScoped()
    {
        SeededPoll first = await SeedPollAsync(PollStatus.Open, [2]);
        SeededPoll second = await SeedPollAsync(PollStatus.Open, [2]);
        using HttpClient client = _factory.CreateHttpsClient();
        await client.GetAsync($"/api/public/polls/{first.Id}");

        Assert.Equal(HttpStatusCode.NoContent, (await VoteAsync(
            client, first.Id, [new(first.QuestionIds[0], first.OptionIds[0][0])])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await VoteAsync(
            client, second.Id, [new(second.QuestionIds[0], second.OptionIds[0][0])])).StatusCode);

        using IServiceScope scope = _factory.Services.CreateScope();
        string[] hashes = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .VoteParticipations
            .Where(item => item.PollId == first.Id || item.PollId == second.Id)
            .Select(item => item.TokenHash)
            .ToArrayAsync();
        Assert.Equal(2, hashes.Length);
        Assert.All(hashes, hash => Assert.Equal(64, hash.Length));
        Assert.NotEqual(hashes[0], hashes[1]);
    }

    [Fact]
    public async Task StoredVotes_HaveNoIdentityLinkAndUseCoarsenedUtcTimestamps()
    {
        SeededPoll poll = await SeedPollAsync(PollStatus.Open, [2]);
        using HttpClient client = _factory.CreateHttpsClient();
        await client.GetAsync($"/api/public/polls/{poll.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await VoteAsync(
            client, poll.Id, [new(poll.QuestionIds[0], poll.OptionIds[0][0])])).StatusCode);

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Vote vote = await db.Votes.SingleAsync(item => item.Option.Question.PollId == poll.Id);
        VoteParticipation participation =
            await db.VoteParticipations.SingleAsync(item => item.PollId == poll.Id);

        Assert.Equal(DateTimeKind.Utc, vote.CreatedAt.Kind);
        Assert.Equal(0, vote.CreatedAt.Minute);
        Assert.Equal(0, vote.CreatedAt.Second);
        Assert.Equal(vote.CreatedAt, participation.UsedAt);
        Assert.DoesNotContain(
            typeof(Vote).GetProperties(),
            property => property.Name.Contains("User", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Participation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingDamagedCookieOrCsrfHeader_Returns400()
    {
        SeededPoll poll = await SeedPollAsync(PollStatus.Open, [2]);
        VoteAnswerRequest[] answers = [new(poll.QuestionIds[0], poll.OptionIds[0][0])];
        using HttpClient noCookieClient = _factory.CreateHttpsClient(handleCookies: false);
        using HttpRequestMessage damagedRequest = CreateVoteRequest(poll.Id, answers);
        damagedRequest.Headers.Add("Cookie", "__Host-AnonPulse.Voter=damaged");
        HttpResponseMessage damaged = await noCookieClient.SendAsync(damagedRequest);

        using HttpClient sessionClient = _factory.CreateHttpsClient();
        await sessionClient.GetAsync($"/api/public/polls/{poll.Id}");
        HttpResponseMessage missingHeader = await sessionClient.PostAsJsonAsync(
            $"/api/public/polls/{poll.Id}/votes",
            new SubmitVotesRequest(answers));

        Assert.Equal(HttpStatusCode.BadRequest, damaged.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingHeader.StatusCode);
        await AssertStoredCountsAsync(poll.Id, 0, 0);
    }

    [Fact]
    public async Task MalformedNestedJson_Returns400Not500()
    {
        SeededPoll poll = await SeedPollAsync(PollStatus.Open, [2]);
        using HttpClient client = _factory.CreateHttpsClient();
        await client.GetAsync($"/api/public/polls/{poll.Id}");
        string[] payloads =
        [
            "{",
            "{\"answers\":[null]}",
            "{\"answers\":{\"questionId\":\"bad\"}}",
            "{\"answers\":[{\"questionId\":{},\"optionId\":null}]}"
        ];

        foreach (string payload in payloads)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/public/polls/{poll.Id}/votes");
            request.Headers.Add("X-AnonPulse-Vote", "1");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    private async Task<SeededPoll> SeedPollAsync(
        PollStatus status,
        IReadOnlyList<int> optionCounts)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var teacher = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"teacher-{Guid.NewGuid()}@example.test",
            Email = $"teacher-{Guid.NewGuid()}@example.test"
        };
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = teacher.Id,
            Title = "Public voting test",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            PublishedAt = status == PollStatus.Open ? DateTime.UtcNow : null,
            ClosedAt = status == PollStatus.Closed ? DateTime.UtcNow : null
        };
        var questionIds = new List<Guid>();
        var optionIds = new List<IReadOnlyList<Guid>>();
        for (int questionIndex = 0; questionIndex < optionCounts.Count; questionIndex++)
        {
            var question = new PollQuestion
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                Text = $"Question {questionIndex}",
                Position = questionIndex
            };
            questionIds.Add(question.Id);
            var currentOptions = new List<Guid>();
            for (int optionIndex = 0; optionIndex < optionCounts[questionIndex]; optionIndex++)
            {
                var option = new PollOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = question.Id,
                    Text = $"Option {questionIndex}-{optionIndex}",
                    Position = optionIndex
                };
                question.Options.Add(option);
                currentOptions.Add(option.Id);
            }

            optionIds.Add(currentOptions);
            poll.Questions.Add(question);
        }

        db.Users.Add(teacher);
        db.Polls.Add(poll);
        await db.SaveChangesAsync();
        return new SeededPoll(poll.Id, questionIds, optionIds);
    }

    private static HttpRequestMessage CreateVoteRequest(
        Guid pollId,
        IReadOnlyList<VoteAnswerRequest> answers)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/public/polls/{pollId}/votes");
        request.Headers.Add("X-AnonPulse-Vote", "1");
        request.Content = JsonContent.Create(new SubmitVotesRequest(
            answers.Cast<VoteAnswerRequest?>().ToArray()));
        return request;
    }

    private static Task<HttpResponseMessage> VoteAsync(
        HttpClient client,
        Guid pollId,
        IReadOnlyList<VoteAnswerRequest> answers)
    {
        return client.SendAsync(CreateVoteRequest(pollId, answers));
    }

    private async Task AssertStoredCountsAsync(
        Guid pollId,
        int votes,
        int participations)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(
            participations,
            await db.VoteParticipations.CountAsync(item => item.PollId == pollId));
        Assert.Equal(
            votes,
            await db.Votes.CountAsync(item =>
                db.PollOptions.Any(option =>
                    option.Id == item.OptionId && option.Question.PollId == pollId)));
    }

    private sealed record SeededPoll(
        Guid Id,
        IReadOnlyList<Guid> QuestionIds,
        IReadOnlyList<IReadOnlyList<Guid>> OptionIds);
}
