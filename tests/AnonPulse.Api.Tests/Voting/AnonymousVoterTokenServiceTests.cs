using AnonPulse.Api.Services.Voting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace AnonPulse.Api.Tests.Voting;

public sealed class AnonymousVoterTokenServiceTests
{
    [Fact]
    public void GeneratedToken_Has256BitsAndPollScopedHashes()
    {
        var service = new AnonymousVoterTokenService(
            new EphemeralDataProtectionProvider(),
            Options.Create(new AnonymousVoterOptions()),
            TimeProvider.System);
        var context = new DefaultHttpContext();

        byte[] token = service.GetOrCreateToken(context);
        string firstHash = service.ComputePollScopedHash(Guid.NewGuid(), token);
        string secondHash = service.ComputePollScopedHash(Guid.NewGuid(), token);

        Assert.Equal(32, token.Length);
        Assert.Equal(64, firstHash.Length);
        Assert.NotEqual(firstHash, secondHash);
        Assert.DoesNotContain(
            Convert.ToBase64String(token),
            context.Response.Headers.SetCookie.ToString());
    }
}
