using AnonPulse.Api.Models;

namespace AnonPulse.Api.Services.Tests;

public static class TestScoreCalculator
{
    public static void Recalculate(NamedAttempt attempt, int testScale)
    {
        attempt.MaximumScore = attempt.Answers.Sum(answer => answer.MaximumScore);
        attempt.RawScore = attempt.Answers.Sum(answer => answer.Score ?? 0m);
        attempt.IsFullyGraded = attempt.Answers.All(answer =>
            answer.GradingState != TestAnswerGradingStates.Pending && answer.Score is not null);
        attempt.NormalizedScore = attempt.IsFullyGraded
            ? decimal.Round(attempt.RawScore / attempt.MaximumScore * testScale, 2,
                MidpointRounding.AwayFromZero)
            : null;
    }
}
