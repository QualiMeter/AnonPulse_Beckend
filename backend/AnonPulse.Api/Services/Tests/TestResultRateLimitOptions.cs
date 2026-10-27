namespace AnonPulse.Api.Services.Tests;

public sealed class TestResultRateLimitOptions
{
    public const string SectionName = "TestResults";
    public int RequestsPerClient { get; set; } = 30;
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);
}
