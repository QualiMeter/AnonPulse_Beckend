using System.Net;
using System.Net.Http.Json;
using AnonPulse.Api.Data;
using AnonPulse.Api.Tests.Voting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class TestResultRateLimitTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_test_result_rate_limit")
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
                ["TestResults:RateLimitWindow"] = "00:10:00",
                ["TestResults:RequestsPerClient"] = "2"
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
    public async Task ResultPolicyIsSeparate_UsesBodySecretAndLimitsPeerAddress()
    {
        using HttpClient client = _factory.CreateHttpsClient();
        string path = $"/api/public/polls/{Guid.NewGuid()}/test-attempts/{Guid.NewGuid()}/result";
        HttpResponseMessage first = await client.PostAsJsonAsync(path,
            new { accessSecret = "invalid" });
        HttpResponseMessage second = await client.PostAsJsonAsync(path,
            new { accessSecret = "invalid" });
        using var thirdRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { accessSecret = "invalid" })
        };
        thirdRequest.Headers.Add("X-Forwarded-For", "203.0.113.10");
        HttpResponseMessage third = await client.SendAsync(thirdRequest);

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.DoesNotContain("invalid", path, StringComparison.Ordinal);
    }
}
