using System.ComponentModel.DataAnnotations;

namespace AnonPulse.Api.Contracts.Auth;

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    bool RememberMe = false);
