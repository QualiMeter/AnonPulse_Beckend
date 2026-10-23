namespace AnonPulse.Api.Contracts.Admin;

public sealed record AdminUserListItemResponse(
    Guid Id,
    string? Email,
    IReadOnlyList<string> Roles,
    bool IsAdministrativelyDeactivated,
    DateTimeOffset? LockoutEnd);

public sealed record AdminUserDetailsResponse(
    Guid Id,
    string? Email,
    IReadOnlyList<string> Roles,
    bool IsAdministrativelyDeactivated,
    DateTimeOffset? LockoutEnd,
    int CreatedPollCount,
    int AuthoredDecisionCount);

public sealed record UpdateAdminUserStatusRequest(bool IsDeactivated);
