using System.Net;
using System.Net.Http.Json;
using AnonPulse.Api.Contracts.Auth;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AnonPulse.Api.Tests.Auth;

public sealed class AuthEndpointsTests
{
    [Fact]
    public async Task Login_WithValidCredentials_ReturnsUserAndSecureCookie()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser seededUser = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { seededUser.Email, seededUser.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CurrentUserResponse? user =
            await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(user);
        Assert.Equal(seededUser.Id, user.Id);
        Assert.Equal(seededUser.Email, user.Email);
        Assert.Contains(RoleNames.Teacher, user.Roles);

        string cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-AnonPulse.Auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_DoesNotRevealWhetherEmailExists()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser seededUser = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();

        HttpResponseMessage wrongPasswordResponse = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { seededUser.Email, Password = "Wrong-password-123!" });
        HttpResponseMessage unknownEmailResponse = await client.PostAsJsonAsync(
            "/api/auth/login",
            new
            {
                Email = "unknown@example.test",
                Password = "Wrong-password-123!"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmailResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            wrongPasswordResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "application/problem+json",
            unknownEmailResponse.Content.Headers.ContentType?.MediaType);

        ProblemDetails? wrongPasswordProblem =
            await wrongPasswordResponse.Content.ReadFromJsonAsync<ProblemDetails>();
        ProblemDetails? unknownEmailProblem =
            await unknownEmailResponse.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(wrongPasswordProblem);
        Assert.NotNull(unknownEmailProblem);
        Assert.Equal(wrongPasswordProblem.Status, unknownEmailProblem.Status);
        Assert.Equal(wrongPasswordProblem.Title, unknownEmailProblem.Title);
    }

    [Fact]
    public async Task Me_WithAuthenticatedSession_ReturnsCurrentUser()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser seededUser = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, seededUser);

        HttpResponseMessage response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CurrentUserResponse? user =
            await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(user);
        Assert.Equal(seededUser.Id, user.Id);
        Assert.Equal(seededUser.Email, user.Email);
        Assert.Equal([RoleNames.Teacher], user.Roles);
    }

    [Fact]
    public async Task Me_WithoutSession_ReturnsUnauthorizedProblemWithoutRedirect()
    {
        await using var factory = new AuthWebApplicationFactory();
        using HttpClient client = factory.CreateHttpsClient();

        HttpResponseMessage response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Logout_EndsCurrentSession()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser seededUser = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        await LoginAsync(client, seededUser);

        HttpResponseMessage logoutResponse =
            await client.PostAsync("/api/auth/logout", content: null);
        HttpResponseMessage meResponse = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            meResponse.Content.Headers.ContentType?.MediaType);
    }

    private static async Task LoginAsync(HttpClient client, SeededUser user)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password });
        response.EnsureSuccessStatusCode();
    }
}
