namespace AnonPulse.Api.Contracts.PollResults;

public sealed record PollResultsResponse(
    Guid Id,
    string Status,
    string ResultsType,
    int TotalParticipants,
    IReadOnlyList<PollQuestionResultsResponse> Questions);

public sealed record PollQuestionResultsResponse(
    Guid Id,
    string Text,
    int Position,
    IReadOnlyList<PollOptionResultsResponse> Options,
    bool AllowsMultipleAnswers = false);

public sealed record PollOptionResultsResponse(
    Guid Id,
    string Text,
    int Position,
    int VoteCount,
    decimal Percentage);
