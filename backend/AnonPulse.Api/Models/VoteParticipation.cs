namespace AnonPulse.Api.Models;

public sealed class VoteParticipation
{
    public Guid Id { get; set; }

    public Guid PollId { get; set; }

    public required string TokenHash { get; set; }

    public DateTime UsedAt { get; set; }

    public Poll Poll { get; set; } = null!;
}
