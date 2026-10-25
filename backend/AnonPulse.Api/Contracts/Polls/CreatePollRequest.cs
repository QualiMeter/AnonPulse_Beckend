namespace AnonPulse.Api.Contracts.Polls;

public sealed record CreatePollRequest
{
    public CreatePollRequest()
    {
    }

    public CreatePollRequest(string? question, IReadOnlyList<string?>? options)
    {
        Question = question;
        Options = options;
    }

    public string? Title { get; init; }

    public string? ResponseMode { get; init; }

    public IReadOnlyList<CreatePollQuestionRequest?>? Questions { get; init; }

    public string? Question { get; init; }

    public IReadOnlyList<string?>? Options { get; init; }
}
