namespace AnonPulse.Api.Services.Voting;

public interface IAnonymousVoterTokenService
{
    byte[] GetOrCreateToken(HttpContext httpContext);

    bool TryGetToken(HttpContext httpContext, out byte[] token);

    string ComputePollScopedHash(Guid pollId, ReadOnlySpan<byte> token);
}
