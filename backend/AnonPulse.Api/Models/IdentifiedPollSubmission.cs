namespace AnonPulse.Api.Models;

public sealed class IdentifiedPollSubmission
{
    public Guid Id { get; set; }

    public Guid PollId { get; set; }

    public required string RespondentName { get; set; }

    public Poll Poll { get; set; } = null!;

    public ICollection<Vote> Votes { get; } = [];

    public ICollection<PollTextAnswer> TextAnswers { get; } = [];
}
