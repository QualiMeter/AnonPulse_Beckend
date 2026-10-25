namespace AnonPulse.Api.Services.FeedbackAnalysis;

public interface IFeedbackAnalysisProvider
{
    string Name { get; }
    Task<ProviderAnalysisResult> AnalyzeAsync(
        FeedbackAnalysisInput input,
        CancellationToken cancellationToken);
}

public sealed record FeedbackAnalysisInput(
    string Model,
    IReadOnlyList<FeedbackInputAnswer> Answers,
    int MaxOutputTokens);

public sealed record FeedbackInputAnswer(string TemporaryId, string Text);

public sealed record ProviderAnalysisResult(
    ProviderReport Report,
    int? PromptTokens,
    int? CompletionTokens);

public sealed record ProviderReport(
    string Summary,
    IReadOnlyList<ProviderTheme> Themes,
    IReadOnlyList<ProviderRecommendation> Recommendations,
    IReadOnlyList<string> Limitations);

public sealed record ProviderTheme(
    string Name,
    string Description,
    string Category,
    IReadOnlyList<string> AnswerIds);

public sealed record ProviderRecommendation(
    string Title,
    string Description,
    string Priority);

public sealed class FeedbackProviderException(
    string errorCode,
    bool retryable,
    TimeSpan? retryAfter = null,
    Exception? innerException = null)
    : Exception(errorCode, innerException)
{
    public string ErrorCode { get; } = errorCode;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
