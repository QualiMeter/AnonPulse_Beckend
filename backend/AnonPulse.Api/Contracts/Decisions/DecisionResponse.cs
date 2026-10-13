namespace AnonPulse.Api.Contracts.Decisions;

public sealed record DecisionResponse(
    Guid Id,
    Guid PollId,
    string Text,
    DateTime PublishedAt);
