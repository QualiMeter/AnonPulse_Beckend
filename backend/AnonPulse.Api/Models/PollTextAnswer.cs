namespace AnonPulse.Api.Models;

public sealed class PollTextAnswer
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public required string Text { get; set; }
    public PollQuestion Question { get; set; } = null!;
}
