using System.Net;
using System.Net.Http.Json;
using AnonPulse.Api.Contracts.Auth;
using AnonPulse.Api.Contracts.KnowledgeGrading;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.KnowledgeGrading;

public sealed class KnowledgeGradingEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_knowledge_api_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N")).Build();
    private Factory _factory = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new Factory(_postgres.GetConnectionString());
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
    public async Task Endpoints_EnforceCsrfOwnershipConsentAndNoStore()
    {
        SeededUser owner = await CreateUserAsync(RoleNames.Teacher);
        SeededUser stranger = await CreateUserAsync(RoleNames.Teacher);
        (Guid pollId, Guid questionId) = await SeedClosedTestAsync(owner.Id);
        using HttpClient ownerClient = _factory.CreateHttpsClient();
        using HttpClient strangerClient = _factory.CreateHttpsClient();
        await LoginAsync(ownerClient, owner);
        await LoginAsync(strangerClient, stranger);

        HttpResponseMessage missingCsrf = await ownerClient.PostAsJsonAsync(
            $"/api/polls/{pollId}/questions/{questionId}/knowledge-grading",
            new { externalProcessingConfirmed = true });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        string csrf = (await ownerClient.GetFromJsonAsync<CsrfTokenResponse>(
            "/api/auth/csrf-token"))!.Token;
        HttpResponseMessage missingConsent = await PostAsync(ownerClient, pollId, questionId,
            false, csrf);
        Assert.Equal(HttpStatusCode.BadRequest, missingConsent.StatusCode);

        HttpResponseMessage foreign = await strangerClient.GetAsync(
            $"/api/polls/{pollId}/questions/{questionId}/knowledge-grading");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        HttpResponseMessage started = await PostAsync(ownerClient, pollId, questionId, true, csrf);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.True(started.Headers.CacheControl?.NoStore);
        KnowledgeGradingJobResponse job = (await started.Content
            .ReadFromJsonAsync<KnowledgeGradingJobResponse>())!;
        Assert.Equal("pending", job.Status);
        Assert.Equal(1, job.AnswerCount);

        HttpResponseMessage status = await ownerClient.GetAsync(
            $"/api/polls/{pollId}/questions/{questionId}/knowledge-grading");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.True(status.Headers.CacheControl?.NoStore);
        HttpResponseMessage results = await ownerClient.GetAsync(
            $"/api/polls/{pollId}/questions/{questionId}/knowledge-grading/results");
        Assert.Equal(HttpStatusCode.Conflict, results.StatusCode);
    }

    private async Task<(Guid PollId, Guid QuestionId)> SeedClosedTestAsync(Guid owner)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = owner,
            Kind = PollKind.Test,
            ResponseMode = PollResponseMode.Identified,
            Status = PollStatus.Open,
            TestScale = 5,
            CreatedAt = DateTime.UtcNow
        };
        var question = new PollQuestion
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Poll = poll,
            Text = "Что такое полиморфизм?",
            Position = 0,
            Type = PollQuestionType.Text,
            TextPurpose = TextQuestionPurpose.Knowledge
        };
        question.KnowledgeSettings = new PollQuestionKnowledgeSettings
        {
            QuestionId = question.Id,
            Question = question,
            ExpectedAnswer = "Общий интерфейс",
            GradingCriteria = "Определение и пример",
            MaxScore = 5
        };
        poll.Questions.Add(question);
        var attempt = new NamedAttempt
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Poll = poll,
            RespondentName = "Студент",
            RespondentGroup = "A",
            ClientRequestId = Guid.NewGuid(),
            RequestHash = new string('a', 64),
            AccessSecretHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            RawScore = 0,
            MaximumScore = 5,
            IsFullyGraded = false,
            IsFinal = false
        };
        attempt.Answers.Add(new NamedAttemptAnswer
        {
            Id = Guid.NewGuid(),
            AttemptId = attempt.Id,
            Attempt = attempt,
            QuestionId = question.Id,
            Question = question,
            Text = "Работа через общий контракт",
            MaximumScore = 5,
            GradingState = TestAnswerGradingStates.Pending
        });
        db.AddRange(poll, attempt);
        await db.SaveChangesAsync();
        poll.Status = PollStatus.Closed;
        poll.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return (poll.Id, question.Id);
    }

    private async Task<SeededUser> CreateUserAsync(string role)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        RoleManager<IdentityRole<Guid>> roles = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        UserManager<ApplicationUser> users = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roles.RoleExistsAsync(role))
            Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);
        string email = $"knowledge-{Guid.NewGuid():N}@example.test";
        const string password = "Knowledge-password-123!";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return new(user.Id, email, password);
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { user.Email, user.Password })).StatusCode);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid pollId,
        Guid questionId, bool confirmed, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/polls/{pollId}/questions/{questionId}/knowledge-grading")
        { Content = JsonContent.Create(new { externalProcessingConfirmed = confirmed }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private sealed class Factory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Ai:Enabled"] = "true",
                    ["Ai:KnowledgeGradingEnabled"] = "true",
                    ["Ai:Groq:ApiKey"] = "fake-test-key",
                    ["Ai:WorkerPollSeconds"] = "60"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ApplicationDbContext>();
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseNpgsql(connectionString));
                services.RemoveAll<IHostedService>();
            });
        }

        public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }
}
