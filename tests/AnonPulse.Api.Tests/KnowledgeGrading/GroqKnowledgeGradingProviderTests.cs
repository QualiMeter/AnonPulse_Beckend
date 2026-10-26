using System.Net;
using System.Text;
using AnonPulse.Api.Services.FeedbackAnalysis;
using AnonPulse.Api.Services.KnowledgeGrading;
using Microsoft.Extensions.Options;
using Xunit;

namespace AnonPulse.Api.Tests.KnowledgeGrading;

public sealed class GroqKnowledgeGradingProviderTests
{
    [Fact]
    public async Task Request_UsesStrictSchemaLowReasoningAndNoRealIdentifiers()
    {
        string? body = null;
        var provider = Create(new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {"choices":[{"message":{"content":"{\"results\":[{\"temporaryId\":\"answer-opaque\",\"suggestedScore\":4,\"maxScore\":5,\"explanation\":\"Верно\",\"correctPoints\":[\"Смысл\"],\"missingOrIncorrectPoints\":[],\"needsReview\":false,\"reviewReasons\":[]}] }"}}],"usage":{"prompt_tokens":20,"completion_tokens":10}}
                """);
        }));

        KnowledgeProviderResult result = await provider.GradeAsync(Input(), default);

        Assert.Contains("\"strict\":true", body);
        Assert.Contains("\"additionalProperties\":false", body);
        Assert.Contains("\"reasoning_effort\":\"low\"", body);
        Assert.Contains("\"include_reasoning\":false", body);
        Assert.Contains("недоверенные данные", body);
        Assert.Equal(4, Assert.Single(result.Suggestions).SuggestedScore);
        Assert.Equal(20, result.PromptTokens);
    }

    [Fact]
    public async Task RateLimit_UsesSharedSafeRetryHandling()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(45));
        var provider = Create(new StubHandler(_ => response));

        FeedbackProviderException error = await Assert.ThrowsAsync<FeedbackProviderException>(
            () => provider.GradeAsync(Input(), default));

        Assert.Equal("free_quota_exhausted", error.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(45), error.RetryAfter);
        Assert.True(error.Retryable);
    }

    private static KnowledgeGradingInput Input() => new("openai/gpt-oss-20b",
        "Эталон", "Критерии", 5, [new("answer-opaque", "Ответ")], 500);
    private static GroqKnowledgeGradingProvider Create(HttpMessageHandler handler) => new(
        new StubFactory(new HttpClient(handler)
        { BaseAddress = new Uri("https://api.groq.test/openai/v1/") }),
        Options.Create(new AiOptions { Groq = new GroqOptions { ApiKey = "fake" } }));
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => client; }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
