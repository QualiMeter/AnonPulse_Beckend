namespace AnonPulse.Api.Models;

public sealed class NamedAttempt
{
    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public required string RespondentName { get; set; }
    public required string RespondentGroup { get; set; }
    public Guid ClientRequestId { get; set; }
    public required string RequestHash { get; set; }
    public required string AccessSecretHash { get; set; }
    public string State { get; set; } = NamedAttemptStates.Completed;
    public DateTime CreatedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public decimal RawScore { get; set; }
    public decimal MaximumScore { get; set; }
    public decimal? NormalizedScore { get; set; }
    public bool IsFullyGraded { get; set; }
    public bool IsFinal { get; set; }
    public Poll Poll { get; set; } = null!;
    public ICollection<NamedAttemptAnswer> Answers { get; } = [];
}

public static class NamedAttemptStates
{
    public const string Completed = "completed";
}
