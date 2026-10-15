namespace AnonPulse.Api.Contracts.PublicPolls;

public sealed record PublicPollResponse(
    Guid Id,
    string? Title,
    IReadOnlyList<PublicPollQuestionResponse> Questions);

public sealed record PublicPollQuestionResponse(
    Guid Id,
    string Text,
    int Position,
    IReadOnlyList<PublicPollOptionResponse> Options,
    bool AllowsMultipleAnswers = false);

public sealed record PublicPollOptionResponse(
    Guid Id,
    string Text,
    int Position);
