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

    private static async Task LoginAsync(HttpClient client, SeededUser user)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password });
        response.EnsureSuccessStatusCode();
    }
}
