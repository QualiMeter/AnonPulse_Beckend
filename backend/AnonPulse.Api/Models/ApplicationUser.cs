using Microsoft.AspNetCore.Identity;

namespace AnonPulse.Api.Models;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public bool IsAdministrativelyDeactivated { get; set; }

    public ICollection<Poll> Polls { get; } = [];

    public ICollection<Decision> Decisions { get; } = [];
}
