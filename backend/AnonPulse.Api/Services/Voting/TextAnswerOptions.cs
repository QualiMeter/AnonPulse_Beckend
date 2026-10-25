namespace AnonPulse.Api.Services.Voting;

public sealed class TextAnswerOptions
{
    public const string SectionName = "TextAnswers";
    public const int StorageMaximumLength = 4000;
    public int MaximumLength { get; set; } = StorageMaximumLength;
    public int MaximumAnswersPerRequest { get; set; } = 200;
    public long MaximumRequestBodySize { get; set; } = 262_144;
}
