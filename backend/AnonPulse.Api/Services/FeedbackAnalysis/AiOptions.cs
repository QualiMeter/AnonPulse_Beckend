namespace AnonPulse.Api.Services.FeedbackAnalysis;

public sealed class AiOptions
{
    public const string SectionName = "Ai";
    public bool Enabled { get; set; }
    public bool KnowledgeGradingEnabled { get; set; }
    public string KnowledgeGradingModel { get; set; } = "openai/gpt-oss-20b";
    public string Provider { get; set; } = "Groq";
    public string Model { get; set; } = "openai/gpt-oss-20b";
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public int MaxConcurrentJobs { get; set; } = 1;
    public int MaxAnswers { get; set; } = 100;
    public int MaxTotalCharacters { get; set; } = 20_000;
    public int MaxInputTokens { get; set; } = 6_000;
    public int MaxOutputTokens { get; set; } = 2_000;
    public string ReasoningEffort { get; set; } = "low";
    public int RetryCooldownSeconds { get; set; } = 60;
    public int WorkerPollSeconds { get; set; } = 5;
    public int KnowledgeMaxAnswers { get; set; } = 50;
    public int KnowledgeMaxTotalCharacters { get; set; } = 20_000;
    public int KnowledgeMaxInputTokens { get; set; } = 6_000;
    public int KnowledgeMaxOutputTokens { get; set; } = 4_000;
    public int KnowledgeMaxReservedTokens { get; set; } = 10_000;
    public int SharedTokenBudget { get; set; } = 100_000;
    public int SharedTokenBudgetPeriodMinutes { get; set; } = 60;
    public int KnowledgeMaxManualRetries { get; set; } = 2;
    public int KnowledgeManualRetryCooldownSeconds { get; set; } = 300;
    public GroqOptions Groq { get; set; } = new();
}

public sealed class GroqOptions
{
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1/";
}
