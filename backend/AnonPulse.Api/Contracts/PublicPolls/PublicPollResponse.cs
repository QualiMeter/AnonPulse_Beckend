namespace AnonPulse.Api.Contracts.PublicPolls;

public sealed record PublicPollResponse(
    Guid Id,
    string? Title,
    IReadOnlyList<PublicPollQuestionResponse> Questions,
    string ResponseMode = "anonymous",
    string Kind = "survey",
    int? TestScale = null);

public sealed record PublicPollQuestionResponse(
    Guid Id,
    string Text,
    int Position,
    IReadOnlyList<PublicPollOptionResponse> Options,
    bool AllowsMultipleAnswers = false,
    string Type = "single_choice",
    string? TextPurpose = null);

public sealed record PublicPollOptionResponse(
    Guid Id,
    string Text,
    int Position);
