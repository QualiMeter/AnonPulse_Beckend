namespace AnonPulse.Api.Contracts.Polls;

public sealed record PollQuestionResponse(
    Guid Id,
    string Text,
    int Position,
    IReadOnlyList<PollOptionResponse> Options);
