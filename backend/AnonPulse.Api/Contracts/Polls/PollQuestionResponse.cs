namespace AnonPulse.Api.Contracts.Polls;

public sealed record PollQuestionResponse(
    Guid Id,
    string Text,
    int Position,
    IReadOnlyList<PollOptionResponse> Options,
    bool AllowsMultipleAnswers = false,
    string Type = "single_choice",
    string? TextPurpose = null,
    KnowledgeQuestionSettingsResponse? KnowledgeSettings = null);

public sealed record KnowledgeQuestionSettingsResponse(
    string ExpectedAnswer,
    string GradingCriteria,
    int MaxScore);
