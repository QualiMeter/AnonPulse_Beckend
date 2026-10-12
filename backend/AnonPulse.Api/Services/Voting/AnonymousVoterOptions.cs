namespace AnonPulse.Api.Services.Voting;

public sealed class AnonymousVoterOptions
{
    public const string SectionName = "AnonymousVoting";

    public string CookieName { get; set; } = "__Host-AnonPulse.Voter";

    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromDays(365);

    public TimeSpan TimestampPrecision { get; set; } = TimeSpan.FromHours(1);

    public int RequestsPerMinute { get; set; } = 30;
}
