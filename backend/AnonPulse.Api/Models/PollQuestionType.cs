namespace AnonPulse.Api.Models;

public enum PollQuestionType
{
    SingleChoice,
    MultipleChoice,
    Text
}

public enum TextQuestionPurpose
{
    Feedback,
    Knowledge
}

public static class PollQuestionTypeValues
{
    public static string ToApiValue(this PollQuestionType type) => type switch
    {
        PollQuestionType.SingleChoice => "single_choice",
        PollQuestionType.MultipleChoice => "multiple_choice",
        PollQuestionType.Text => "text",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static string? ToApiValue(this TextQuestionPurpose? purpose) => purpose switch
    {
        TextQuestionPurpose.Feedback => "feedback",
        TextQuestionPurpose.Knowledge => "knowledge",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };
}
