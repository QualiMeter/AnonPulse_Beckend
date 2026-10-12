namespace AnonPulse.Api.Contracts.Polls;

public sealed record CreatePollQuestionRequest(
    string? Text,
    IReadOnlyList<string?>? Options);
