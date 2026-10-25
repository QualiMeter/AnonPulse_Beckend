namespace AnonPulse.Api.Contracts.Polls;

public sealed record CreatePollQuestionRequest(
    string? Text,
    IReadOnlyList<string?>? Options,
    bool? AllowsMultipleAnswers = null,
    string? Type = null,
    string? TextPurpose = null,
    KnowledgeQuestionSettingsRequest? KnowledgeSettings = null);

public sealed record KnowledgeQuestionSettingsRequest(
    string? ExpectedAnswer,
    string? GradingCriteria,
    int? MaxScore);
