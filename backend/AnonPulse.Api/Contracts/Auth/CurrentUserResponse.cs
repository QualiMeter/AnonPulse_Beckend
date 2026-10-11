namespace AnonPulse.Api.Contracts.Auth;

public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    IReadOnlyCollection<string> Roles);
