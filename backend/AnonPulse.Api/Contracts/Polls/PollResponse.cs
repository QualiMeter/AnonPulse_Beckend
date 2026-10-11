namespace AnonPulse.Api.Contracts.Polls;

public sealed record PollResponse(
    Guid Id,
    string Question,
    string Status,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    DateTime? ClosedAt,
    IReadOnlyList<PollOptionResponse> Options);
