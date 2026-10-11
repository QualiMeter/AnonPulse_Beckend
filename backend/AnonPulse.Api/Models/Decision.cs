namespace AnonPulse.Api.Models;

public sealed class Decision
{
    public Guid Id { get; set; }

    public Guid PollId { get; set; }

    public Guid AuthorId { get; set; }

    public required string Text { get; set; }

    public DateTime? PublishedAt { get; set; }

    public Poll Poll { get; set; } = null!;

    public ApplicationUser Author { get; set; } = null!;
}
