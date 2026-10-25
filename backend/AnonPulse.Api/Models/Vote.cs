namespace AnonPulse.Api.Models;

public sealed class Vote
{
    public Guid Id { get; set; }

    public Guid OptionId { get; set; }

    public DateTime? CreatedAt { get; set; }

    public Guid? IdentifiedSubmissionId { get; set; }

    public PollOption Option { get; set; } = null!;

    public IdentifiedPollSubmission? IdentifiedSubmission { get; set; }
}
