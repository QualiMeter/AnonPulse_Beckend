namespace AnonPulse.Api.Contracts.Polls;

public sealed record PollResponse(
    Guid Id,
    string? Title,
    string Status,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    DateTime? ClosedAt,
    IReadOnlyList<PollQuestionResponse> Questions,
    string? Question,
    IReadOnlyList<PollOptionResponse> Options);
