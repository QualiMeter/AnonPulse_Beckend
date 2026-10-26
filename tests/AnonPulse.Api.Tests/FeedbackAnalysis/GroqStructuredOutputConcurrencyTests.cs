using System.Net;
using System.Text;
using AnonPulse.Api.Services.FeedbackAnalysis;
using AnonPulse.Api.Services.KnowledgeGrading;
using Microsoft.Extensions.Options;
using Xunit;

namespace AnonPulse.Api.Tests.FeedbackAnalysis;

public sealed class GroqStructuredOutputConcurrencyTests
{
    [Fact]
    public async Task SharedGate_IsHeldUntilResponseBodyIsConsumedAcrossBothScenarios()
    {
        var firstBodyMayFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        var handler = new AsyncHandler(async request =>
        {
            int number = Interlocked.Increment(ref entered);
            if (number == 1) firstRequestEntered.SetResult();
            string requestBody = await request.Content!.ReadAsStringAsync();
            string content = requestBody.Contains("knowledge_grading", StringComparison.Ordinal)
                ? """{"results":[{"temporaryId":"answer-opaque","suggestedScore":1,"maxScore":5,"explanation":"ok","correctPoints":[],"missingOrIncorrectPoints":[],"needsReview":false,"reviewReasons":[]}]}"""
                : """{"summary":"ok","themes":[],"recommendations":[],"limitations":[]}""";
            string envelope = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content } } },
                usage = new { prompt_tokens = 1, completion_tokens = 1 }
            });
            HttpContent responseContent = number == 1
                ? new BlockingContent(envelope, firstBodyMayFinish.Task)
                : new StringContent(envelope, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent };
        });
        var client = new HttpClient(handler)
        { BaseAddress = new Uri("https://api.groq.test/openai/v1/") };
        var factory = new StubFactory(client);
        var settings = Options.Create(new AiOptions
        {
            MaxConcurrentJobs = 1,
            Groq = new GroqOptions { ApiKey = "fake" }
        });
        var feedback = new GroqFeedbackAnalysisProvider(factory, settings);
        var knowledge = new GroqKnowledgeGradingProvider(factory, settings);

        Task<ProviderAnalysisResult> first = feedback.AnalyzeAsync(
            new("openai/gpt-oss-20b", [new("answer-feedback", "text")], 100), default);
        await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<KnowledgeProviderResult> second = knowledge.GradeAsync(
            new("openai/gpt-oss-20b", "expected", "criteria", 5,
                [new("answer-opaque", "text")], 100), default);

        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref entered));
        Assert.False(second.IsCompleted);

        firstBodyMayFinish.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, entered);
    }

    private sealed class BlockingContent(string value, Task release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream,
            TransportContext? context)
        {
            await release;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(value));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class AsyncHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
