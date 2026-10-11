namespace AnonPulse.Api.Contracts.Polls;

public sealed record CreatePollRequest(
    string? Question,
    IReadOnlyList<string?>? Options);
