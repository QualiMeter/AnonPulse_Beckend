namespace AnonPulse.Api.Models;

public sealed class PollOption
{
    public Guid Id { get; set; }

    public Guid QuestionId { get; set; }

    public required string Text { get; set; }

    public int Position { get; set; }

    public PollQuestion Question { get; set; } = null!;

    public ICollection<Vote> Votes { get; } = [];

    public ICollection<PollQuestionCorrectOption> CorrectForQuestions { get; } = [];

    public ICollection<NamedAttemptAnswerOption> TestAnswerSelections { get; } = [];
}
