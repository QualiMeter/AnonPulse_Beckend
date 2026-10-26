namespace AnonPulse.Api.Services.Tests;

public sealed class TestSubmissionRateLimitOptions
{
    public const string SectionName = "TestSubmissions";

    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    public int RequestsPerClient { get; set; } = 120;
}
