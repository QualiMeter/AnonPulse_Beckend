namespace AnonPulse.Api.Services.FeedbackAnalysis;

public sealed class AiOptions
{
    public const string SectionName = "Ai";
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Groq";
    public string Model { get; set; } = "openai/gpt-oss-20b";
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public int MaxConcurrentJobs { get; set; } = 1;
    public int MaxAnswers { get; set; } = 100;
    public int MaxTotalCharacters { get; set; } = 20_000;
    public int MaxInputTokens { get; set; } = 6_000;
    public int MaxOutputTokens { get; set; } = 2_000;
    public int WorkerPollSeconds { get; set; } = 5;
    public GroqOptions Groq { get; set; } = new();
}

public sealed class GroqOptions
{
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1/";
}
