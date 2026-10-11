using Microsoft.AspNetCore.Identity;

namespace AnonPulse.Api.Models;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ICollection<Poll> Polls { get; } = [];

    public ICollection<Decision> Decisions { get; } = [];
}
