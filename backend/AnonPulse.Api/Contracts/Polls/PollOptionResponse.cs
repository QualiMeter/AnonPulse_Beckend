namespace AnonPulse.Api.Contracts.Polls;

public sealed record PollOptionResponse(
    Guid Id,
    string Text,
    int Position);
