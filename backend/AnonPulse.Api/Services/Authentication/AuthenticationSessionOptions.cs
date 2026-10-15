namespace AnonPulse.Api.Services.Authentication;

public sealed class AuthenticationSessionOptions
{
    public const string SectionName = "AuthenticationSession";

    public static readonly TimeSpan MaximumPersistentLifetime = TimeSpan.FromDays(30);

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    public TimeSpan PersistentLifetime { get; set; } = MaximumPersistentLifetime;
}
