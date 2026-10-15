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
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithRememberMe_IssuesPersistentCookieForAtMostThirtyDays()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        await using var factory = new AuthWebApplicationFactory(clock);
        SeededUser user = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password, RememberMe = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string cookie = GetAuthenticationCookie(response);
        DateTimeOffset expires = ParseCookieExpires(cookie);
        Assert.InRange(
            expires,
            clock.GetUtcNow().AddDays(30).AddSeconds(-1),
            clock.GetUtcNow().AddDays(30).AddSeconds(1));
    }

    [Fact]
    public async Task Login_WhenTwoFactorIsRequired_DoesNotIssueApplicationCookie()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser user = await factory.CreateTwoFactorTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password, RememberMe = true });
        HttpResponseMessage me = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                "__Host-AnonPulse.Auth=",
                StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        ProblemDetails? problem =
            await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Two-factor authentication is required.", problem.Title);
    }

    [Fact]
    public async Task SessionTicket_ExpiresAfterConfiguredServerLifetime()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        await using var factory = new AuthWebApplicationFactory(clock);
        SeededUser user = await factory.CreateTeacherAsync();
        using HttpClient loginClient = factory.CreateHttpsClient();
        HttpResponseMessage login = await loginClient.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password });
        string cookie = CookiePair(GetAuthenticationCookie(login));
        clock.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(1)));
        using HttpClient reopenedClient = factory.CreateHttpsClient(handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);

        HttpResponseMessage response = await reopenedClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PersistentCookie_SurvivesBrowserReopenAndSlidesBeforeExpiry()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        await using var factory = new AuthWebApplicationFactory(clock);
        SeededUser user = await factory.CreateTeacherAsync();
        using HttpClient loginClient = factory.CreateHttpsClient();
        HttpResponseMessage login = await loginClient.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password, RememberMe = true });
        string originalHeader = GetAuthenticationCookie(login);
        string cookie = CookiePair(originalHeader);
        DateTimeOffset originalExpiry = ParseCookieExpires(originalHeader);
        clock.Advance(TimeSpan.FromDays(16));
        using HttpClient reopenedClient = factory.CreateHttpsClient(handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);

        HttpResponseMessage response = await reopenedClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string renewedHeader = GetAuthenticationCookie(response);
        DateTimeOffset renewedExpiry = ParseCookieExpires(renewedHeader);
        Assert.True(renewedExpiry > originalExpiry);
        Assert.InRange(
            renewedExpiry,
            clock.GetUtcNow().AddDays(30).AddSeconds(-1),
            clock.GetUtcNow().AddDays(30).AddSeconds(1));
    }

    [Fact]
    public async Task ActiveSession_SlidesWithoutBecomingPersistent()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        await using var factory = new AuthWebApplicationFactory(clock);
        SeededUser user = await factory.CreateTeacherAsync();
        using HttpClient loginClient = factory.CreateHttpsClient();
        HttpResponseMessage login = await loginClient.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password });
        string cookie = CookiePair(GetAuthenticationCookie(login));
        clock.Advance(TimeSpan.FromHours(5));
        using HttpClient client = factory.CreateHttpsClient(handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string renewedHeader = GetAuthenticationCookie(response);
        Assert.DoesNotContain("expires=", renewedHeader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", renewedHeader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_EndsPersistentSession()
    {
        await using var factory = new AuthWebApplicationFactory();
        SeededUser user = await factory.CreateTeacherAsync();
        using HttpClient client = factory.CreateHttpsClient();
        HttpResponseMessage login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { user.Email, user.Password, RememberMe = true });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        HttpResponseMessage logout =
            await client.PostAsync("/api/auth/logout", content: null);
        HttpResponseMessage me = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
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

    private static string GetAuthenticationCookie(HttpResponseMessage response) =>
        Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-AnonPulse.Auth=", StringComparison.Ordinal));

    private static string CookiePair(string setCookie) => setCookie.Split(';', 2)[0];

    private static DateTimeOffset ParseCookieExpires(string setCookie)
    {
        string expires = Assert.Single(
            setCookie.Split(';', StringSplitOptions.TrimEntries),
            part => part.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        return DateTimeOffset.Parse(
            expires["expires=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }
}
