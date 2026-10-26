namespace AnonPulse.Api.Contracts.PublicPolls;

public sealed record SubmitTestRequest(
    string? RespondentName,
    string? RespondentGroup,
    Guid ClientRequestId,
    IReadOnlyList<TestAnswerRequest?>? Answers);

public sealed record TestAnswerRequest(
    Guid QuestionId,
    Guid? OptionId = null,
    string? Text = null);

public sealed record TestSubmissionCreatedResponse(
    Guid AttemptId,
    string AccessSecret);
