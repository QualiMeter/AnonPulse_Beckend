using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.Voting;

public sealed class AnonymousVotingSessionRateLimiter
    : IAnonymousVotingSessionRateLimiter
{
    private static readonly byte[] KeyDomain =
        Encoding.UTF8.GetBytes("AnonPulse.VotingRateLimit.v1\0");

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionWindow> _sessions = new();
    private readonly AnonymousVoterOptions _options;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _nextPruneAt = DateTimeOffset.MinValue;

    public AnonymousVotingSessionRateLimiter(
        IOptions<AnonymousVoterOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public VotingRateLimitDecision TryAcquire(ReadOnlySpan<byte> anonymousToken)
    {
        string key = ComputeKey(anonymousToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out SessionWindow? session))
            {
                if (_sessions.Count >= _options.MaxTrackedSessions)
                {
                    PruneExpiredSessions(now);
                    if (_sessions.Count >= _options.MaxTrackedSessions)
                    {
                        return new VotingRateLimitDecision(
                            false,
                            _options.RateLimitWindow,
                            "The anonymous-session rate-limit capacity is temporarily full.");
                    }
                }

                session = new SessionWindow(now);
                _sessions.Add(key, session);
            }

            session.LastSeenAt = now;
            if (now - session.WindowStartedAt >= _options.RateLimitWindow)
            {
                session.WindowStartedAt = now;
                session.RequestCount = 0;
            }

            if (session.RequestCount >= _options.SessionRequestsPerWindow)
            {
                TimeSpan retryAfter = _options.RateLimitWindow -
                                      (now - session.WindowStartedAt);
                return new VotingRateLimitDecision(
                    false,
                    retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero,
                    "Too many vote submissions from this anonymous session.");
            }

            session.RequestCount++;
            return new VotingRateLimitDecision(true, TimeSpan.Zero);
        }
    }

    private void PruneExpiredSessions(DateTimeOffset now)
    {
        if (now < _nextPruneAt)
        {
            return;
        }

        DateTimeOffset expiresBefore = now - _options.SessionTrackingRetention;
        string[] expiredKeys = _sessions
            .Where(item => item.Value.LastSeenAt <= expiresBefore)
            .Select(item => item.Key)
            .ToArray();
        foreach (string expiredKey in expiredKeys)
        {
            _sessions.Remove(expiredKey);
        }

        _nextPruneAt = now + _options.RateLimitWindow;
    }

    private static string ComputeKey(ReadOnlySpan<byte> token)
    {
        byte[] input = new byte[KeyDomain.Length + token.Length];
        KeyDomain.CopyTo(input, 0);
        token.CopyTo(input.AsSpan(KeyDomain.Length));
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private sealed class SessionWindow(DateTimeOffset now)
    {
        public DateTimeOffset WindowStartedAt { get; set; } = now;

        public DateTimeOffset LastSeenAt { get; set; } = now;

        public int RequestCount { get; set; }
    }
}
