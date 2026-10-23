using AnonPulse.Api.Contracts.Polls;

namespace AnonPulse.Api.Contracts.Admin;

public sealed record AdminPollListItemResponse(
    Guid Id,
    string? Title,
    string Status,
    Guid CreatorId,
    string? CreatorEmail,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    DateTime? ClosedAt,
    int QuestionCount,
    int ParticipationCount,
    bool HasPublishedDecision);

public sealed record AdminPollDetailsResponse(
    Guid Id,
    string? Title,
    string Status,
    Guid CreatorId,
    string? CreatorEmail,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    DateTime? ClosedAt,
    IReadOnlyList<PollQuestionResponse> Questions,
    int ParticipationCount,
    int VoteCount,
    bool HasPublishedDecision);
