namespace AnonPulse.Api.Contracts.Admin;

public sealed record AdminAuditResponse(
    Guid Id,
    Guid AdministratorId,
    string ActionType,
    string TargetType,
    Guid? TargetId,
    DateTime OccurredAt,
    IReadOnlyDictionary<string, string> Metadata);
