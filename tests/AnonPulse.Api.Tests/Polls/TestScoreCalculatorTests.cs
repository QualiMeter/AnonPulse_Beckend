using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Tests;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class TestScoreCalculatorTests
{
    [Fact]
    public void Recalculate_UsesAwayFromZeroAtNumericTie()
    {
        var attempt = new NamedAttempt
        {
            Id = Guid.NewGuid(),
            PollId = Guid.NewGuid(),
            RespondentName = "Student",
            RespondentGroup = "Group",
            ClientRequestId = Guid.NewGuid(),
            RequestHash = new string('a', 64),
            AccessSecretHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        attempt.Answers.Add(new NamedAttemptAnswer
        {
            Id = Guid.NewGuid(),
            AttemptId = attempt.Id,
            QuestionId = Guid.NewGuid(),
            MaximumScore = 10m,
            Score = 1.01m,
            GradingState = TestAnswerGradingStates.TeacherGraded
        });

        TestScoreCalculator.Recalculate(attempt, 5);

        Assert.Equal(1.01m, attempt.RawScore);
        Assert.Equal(10m, attempt.MaximumScore);
        Assert.Equal(0.51m, attempt.NormalizedScore);
        Assert.True(attempt.IsFullyGraded);
    }
}
