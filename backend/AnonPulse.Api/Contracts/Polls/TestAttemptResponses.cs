namespace AnonPulse.Api.Contracts.Polls;

public sealed record TestAttemptResponse(
    Guid Id,
    string RespondentName,
    string RespondentGroup,
    string State,
    DateTime CreatedAt,
    DateTime CompletedAt,
    decimal RawScore,
    decimal MaximumScore,
    decimal? NormalizedScore,
    bool IsFullyGraded,
    bool IsFinal,
    IReadOnlyList<TestAttemptAnswerResponse> Answers);

public sealed record TestAttemptAnswerResponse(
    Guid QuestionId,
    IReadOnlyList<Guid> SelectedOptionIds,
    string? Text,
    string GradingState,
    decimal? Score,
    decimal MaximumScore);
