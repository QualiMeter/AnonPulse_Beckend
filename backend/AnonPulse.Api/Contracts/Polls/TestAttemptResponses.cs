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
    decimal MaximumScore,
    Guid AnswerId);

public sealed record GradeTestAnswerRequest(decimal? Score, Guid? SuggestionId);

public sealed record GradeTestAnswerResponse(
    Guid AnswerId,
    decimal Score,
    string GradingState);

public sealed record FinalizeTestAttemptResponse(
    Guid AttemptId,
    decimal RawScore,
    decimal MaximumScore,
    decimal NormalizedScore,
    bool IsFullyGraded,
    bool IsFinal,
    Guid FinalizedByUserId,
    DateTime FinalizedAt);

public sealed record TestGradeChangeResponse(
    Guid Id,
    Guid AttemptId,
    Guid AnswerId,
    Guid ActorId,
    decimal? OldScore,
    decimal NewScore,
    string Source,
    Guid? SuggestionId,
    DateTime ChangedAt);

public sealed record TestAttemptGradeHistoryResponse(
    Guid AttemptId,
    IReadOnlyList<TestGradeChangeResponse> Changes,
    Guid? FinalizedByUserId,
    DateTime? FinalizedAt);
