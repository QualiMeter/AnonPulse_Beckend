namespace AnonPulse.Api.Models;

public sealed class Poll
{
    public Guid Id { get; set; }

    public Guid CreatorId { get; set; }

    public string? Title { get; set; }

    public PollStatus Status { get; set; } = PollStatus.Draft;

    public DateTime CreatedAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public uint Version { get; set; }

    public ApplicationUser Creator { get; set; } = null!;

    public ICollection<PollQuestion> Questions { get; } = [];

    public ICollection<VoteParticipation> VoteParticipations { get; } = [];

    public Decision? Decision { get; set; }
}
