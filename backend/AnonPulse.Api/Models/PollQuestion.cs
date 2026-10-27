namespace AnonPulse.Api.Models;

public sealed class PollQuestion
{
    private bool _allowsMultipleAnswers;
    private PollQuestionType _type = PollQuestionType.SingleChoice;
    public Guid Id { get; set; }

    public Guid PollId { get; set; }

    public required string Text { get; set; }

    public int Position { get; set; }

    public bool AllowsMultipleAnswers
    {
        get => _allowsMultipleAnswers;
        set
        {
            _allowsMultipleAnswers = value;
            if (_type != PollQuestionType.Text)
            {
                _type = value
                    ? PollQuestionType.MultipleChoice
                    : PollQuestionType.SingleChoice;
            }
        }
    }

    public PollQuestionType Type
    {
        get => _type;
        set
        {
            _type = value;
            _allowsMultipleAnswers = value == PollQuestionType.MultipleChoice;
        }
    }

    public TextQuestionPurpose? TextPurpose { get; set; }

    public decimal? TestWeight { get; set; }

    public Poll Poll { get; set; } = null!;

    public ICollection<PollOption> Options { get; } = [];

    public ICollection<PollTextAnswer> TextAnswers { get; } = [];

    public PollQuestionKnowledgeSettings? KnowledgeSettings { get; set; }

    public ICollection<PollQuestionCorrectOption> CorrectOptions { get; } = [];

    public ICollection<PollFeedbackAnalysis> FeedbackAnalyses { get; } = [];

    public ICollection<PollKnowledgeGradingJob> KnowledgeGradingJobs { get; } = [];
}
