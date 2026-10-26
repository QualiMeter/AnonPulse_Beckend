using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

internal static class GroqStructuredOutputClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();

    internal static async Task<GroqStructuredOutputResponse> SendAsync(
        IHttpClientFactory factory,
        AiOptions settings,
        object payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Groq.ApiKey))
            throw new FeedbackProviderException("ai_not_configured", false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Groq.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        SemaphoreSlim gate = Gates.GetOrAdd(settings.MaxConcurrentJobs,
            limit => new SemaphoreSlim(limit, limit));
        await gate.WaitAsync(cancellationToken);
        try
        {
            response = await factory.CreateClient("groq-ai")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FeedbackProviderException("provider_timeout", true, null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new FeedbackProviderException("provider_unavailable", true, null, exception);
        }
        finally
        {
            gate.Release();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
                    retryAfter = date - DateTimeOffset.UtcNow;
                throw new FeedbackProviderException("free_quota_exhausted", true,
                    retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMinutes(1));
            }
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                    throw new FeedbackProviderException("provider_token_limit_exceeded", false);
                bool retryable = (int)response.StatusCode >= 500;
                throw new FeedbackProviderException(
                    retryable ? "provider_unavailable" : "provider_rejected_request", retryable);
            }

            try
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument envelope = await JsonDocument.ParseAsync(body,
                    cancellationToken: cancellationToken);
                JsonElement root = envelope.RootElement;
                string content = root.GetProperty("choices")[0].GetProperty("message")
                    .GetProperty("content").GetString()!;
                return new(content, TryGetInt(root, "prompt_tokens"),
                    TryGetInt(root, "completion_tokens"));
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                               InvalidOperationException or IndexOutOfRangeException)
            {
                throw new FeedbackProviderException("invalid_provider_response", false, null, exception);
            }
        }
    }

    private static int? TryGetInt(JsonElement root, string name) =>
        root.TryGetProperty("usage", out JsonElement usage) &&
        usage.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed : null;
}

internal sealed record GroqStructuredOutputResponse(
    string Content, int? PromptTokens, int? CompletionTokens);
