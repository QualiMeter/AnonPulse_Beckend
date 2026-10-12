namespace AnonPulse.Api.Services.Voting;

public interface IAnonymousVotingSessionRateLimiter
{
    VotingRateLimitDecision TryAcquire(ReadOnlySpan<byte> anonymousToken);
}

public sealed record VotingRateLimitDecision(
    bool IsAllowed,
    TimeSpan RetryAfter,
    string? Reason = null);
