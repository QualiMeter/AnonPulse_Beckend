using System.Net;
using System.Text;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.Extensions.Options;
using Xunit;

namespace AnonPulse.Api.Tests.FeedbackAnalysis;

public sealed class GroqFeedbackAnalysisProviderTests
{
    [Fact]
    public async Task RateLimit_ProducesRetryableSafeErrorAndHonorsRetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(37));
        var provider = CreateProvider(new StubHandler(_ => response));

        FeedbackProviderException error = await Assert.ThrowsAsync<FeedbackProviderException>(() =>
            provider.AnalyzeAsync(Input(), default));

        Assert.Equal("free_quota_exhausted", error.ErrorCode);
        Assert.True(error.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(37), error.RetryAfter);
    }

    [Fact]
    public async Task MalformedSuccessfulResponse_IsRejectedWithoutLoggingBody()
    {
        var provider = CreateProvider(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json")
        }));
        FeedbackProviderException error = await Assert.ThrowsAsync<FeedbackProviderException>(() =>
            provider.AnalyzeAsync(Input(), default));
        Assert.Equal("invalid_provider_response", error.ErrorCode);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Request_UsesStrictJsonSchemaAndConfiguredModel()
    {
        string? body = null;
        var provider = CreateProvider(new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"choices":[{"message":{"content":"{\"summary\":\"Итог\",\"themes\":[],\"recommendations\":[],\"limitations\":[]}"}}],"usage":{"prompt_tokens":12,"completion_tokens":7}}
                    """, Encoding.UTF8, "application/json")
            };
        }));

        ProviderAnalysisResult result = await provider.AnalyzeAsync(Input(), default);

        Assert.Contains("\"type\":\"json_schema\"", body);
        Assert.Contains("\"strict\":true", body);
        Assert.Contains("\"model\":\"openai/gpt-oss-20b\"", body);
        Assert.Contains("\"reasoning_effort\":\"low\"", body);
        Assert.Contains("\"include_reasoning\":false", body);
        Assert.DoesNotContain("reasoning_format", body);
        Assert.Equal(12, result.PromptTokens);
    }

    [Fact]
    public async Task ProviderPayloadTooLarge_IsNotRetried()
    {
        var provider = CreateProvider(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)));

        FeedbackProviderException error = await Assert.ThrowsAsync<FeedbackProviderException>(() =>
            provider.AnalyzeAsync(Input(), default));

        Assert.Equal("provider_token_limit_exceeded", error.ErrorCode);
        Assert.False(error.Retryable);
    }

    private static FeedbackAnalysisInput Input() => new("openai/gpt-oss-20b",
        [new("opaque-1", "Отзыв")], 1000);

    private static GroqFeedbackAnalysisProvider CreateProvider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.groq.test/openai/v1/") };
        return new GroqFeedbackAnalysisProvider(new StubFactory(client), Options.Create(new AiOptions
        {
            Enabled = true,
            Groq = new GroqOptions { ApiKey = "fake-test-key" }
        }));
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
