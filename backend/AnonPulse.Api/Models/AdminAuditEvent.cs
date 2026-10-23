namespace AnonPulse.Api.Models;

public sealed class AdminAuditEvent
{
    public Guid Id { get; set; }

    public Guid AdministratorId { get; set; }

    public required string ActionType { get; set; }

    public required string TargetType { get; set; }

    public Guid? TargetId { get; set; }

    public DateTime OccurredAt { get; set; }

    public required string MetadataJson { get; set; }
}
