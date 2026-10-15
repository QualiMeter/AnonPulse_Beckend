namespace AnonPulse.Api.Models;

public sealed class PollQuestion
{
    public Guid Id { get; set; }

    public Guid PollId { get; set; }

    public required string Text { get; set; }

    public int Position { get; set; }

    public bool AllowsMultipleAnswers { get; set; }

    public Poll Poll { get; set; } = null!;

    public ICollection<PollOption> Options { get; } = [];
}
