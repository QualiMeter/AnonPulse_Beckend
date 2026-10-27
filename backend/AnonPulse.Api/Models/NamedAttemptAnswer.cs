namespace AnonPulse.Api.Models;

public sealed class NamedAttemptAnswer
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid QuestionId { get; set; }
    public string? Text { get; set; }
    public string GradingState { get; set; } = TestAnswerGradingStates.Pending;
    public decimal? Score { get; set; }
    public decimal MaximumScore { get; set; }
    public NamedAttempt Attempt { get; set; } = null!;
    public PollQuestion Question { get; set; } = null!;
    public ICollection<NamedAttemptAnswerOption> SelectedOptions { get; } = [];
}

public static class TestAnswerGradingStates
{
    public const string AutoGraded = "auto_graded";
    public const string Pending = "pending";
    public const string TeacherGraded = "teacher_graded";
}
