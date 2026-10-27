namespace AnonPulse.Api.Services.KnowledgeGrading;

public interface IKnowledgeGradingProvider
{
    Task<KnowledgeProviderResult> GradeAsync(
        KnowledgeGradingInput input, CancellationToken cancellationToken);
}

public sealed record KnowledgeGradingInput(
    string Model,
    string ExpectedAnswer,
    string GradingCriteria,
    int MaxScore,
    IReadOnlyList<KnowledgeInputAnswer> Answers,
    int MaxOutputTokens);

public sealed record KnowledgeInputAnswer(string TemporaryId, string Text);

public sealed record KnowledgeProviderResult(
    IReadOnlyList<KnowledgeProviderSuggestion> Suggestions,
    int? PromptTokens,
    int? CompletionTokens);

public sealed record KnowledgeProviderSuggestion(
    string TemporaryId,
    int SuggestedScore,
    int MaxScore,
    string Explanation,
    IReadOnlyList<string> CorrectPoints,
    IReadOnlyList<string> MissingOrIncorrectPoints,
    bool NeedsReview,
    IReadOnlyList<string> ReviewReasons);
