namespace AnonPulse.Api.Models;

public sealed class NamedAttemptAnswerOption
{
    public Guid AnswerId { get; set; }
    public Guid OptionId { get; set; }
    public NamedAttemptAnswer Answer { get; set; } = null!;
    public PollOption Option { get; set; } = null!;
}
