namespace AnonPulse.Api.Contracts.PublicPolls;

public sealed record SubmitVotesRequest(
    IReadOnlyList<VoteAnswerRequest?>? Answers,
    string? RespondentName = null);

public sealed record VoteAnswerRequest(
    Guid QuestionId,
    Guid? OptionId = null,
    string? Text = null);
