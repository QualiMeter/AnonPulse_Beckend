namespace AnonPulse.Api.Contracts.PublicPolls;

public sealed record SubmitVotesRequest(
    IReadOnlyList<VoteAnswerRequest?>? Answers);

public sealed record VoteAnswerRequest(
    Guid QuestionId,
    Guid OptionId);
