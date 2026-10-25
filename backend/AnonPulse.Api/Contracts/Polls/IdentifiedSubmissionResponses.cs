namespace AnonPulse.Api.Contracts.Polls;

public sealed record IdentifiedPollSubmissionResponse(
    Guid Id,
    string RespondentName,
    IReadOnlyList<IdentifiedPollAnswerResponse> Answers);

public sealed record IdentifiedPollAnswerResponse(
    Guid QuestionId,
    IReadOnlyList<Guid> SelectedOptionIds,
    string? Text);
