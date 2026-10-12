namespace AnonPulse.Api.Services.Voting;

public sealed class AnonymousVoterOptions
{
    public const string SectionName = "AnonymousVoting";

    public string CookieName { get; set; } = "__Host-AnonPulse.Voter";

    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromDays(365);

    public TimeSpan TimestampPrecision { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    public int SessionRequestsPerWindow { get; set; } = 10;

    public int GlobalRequestsPerWindow { get; set; } = 600;

    public int MaxTrackedSessions { get; set; } = 10_000;

    public TimeSpan SessionTrackingRetention { get; set; } = TimeSpan.FromMinutes(5);
}
