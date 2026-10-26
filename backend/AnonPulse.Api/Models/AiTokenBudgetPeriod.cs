namespace AnonPulse.Api.Models;

public sealed class AiTokenBudgetPeriod
{
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int TokenLimit { get; set; }
    public int ReservedTokens { get; set; }
    public int EstimatedConsumedTokens { get; set; }
    public int ActualTokens { get; set; }
    public ICollection<AiTokenBudgetReservation> Reservations { get; } = [];
}

public sealed class AiTokenBudgetReservation
{
    public Guid Id { get; set; }
    public DateTime PeriodStart { get; set; }
    public required string WorkloadType { get; set; }
    public Guid WorkloadId { get; set; }
    public int AttemptNumber { get; set; }
    public int ReservedTokens { get; set; }
    public int? ActualPromptTokens { get; set; }
    public int? ActualCompletionTokens { get; set; }
    public int ChargedTokens { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SettledAt { get; set; }
    public AiTokenBudgetPeriod Period { get; set; } = null!;
}
