using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.Voting;

public sealed class AnonymousVoterTokenService : IAnonymousVoterTokenService
{
    private const string ProtectorPurpose = "AnonPulse.AnonymousVoting.Cookie.v1";
    private static readonly byte[] HashDomain =
        Encoding.UTF8.GetBytes("AnonPulse.VoteParticipation.v1\0");

    private readonly ITimeLimitedDataProtector _protector;
    private readonly AnonymousVoterOptions _options;
    private readonly TimeProvider _timeProvider;

    public AnonymousVoterTokenService(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<AnonymousVoterOptions> options,
        TimeProvider timeProvider)
    {
        _protector = dataProtectionProvider
            .CreateProtector(ProtectorPurpose)
            .ToTimeLimitedDataProtector();
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public byte[] GetOrCreateToken(HttpContext httpContext)
    {
        if (TryGetToken(httpContext, out byte[] existingToken))
        {
            return existingToken;
        }

        byte[] token = RandomNumberGenerator.GetBytes(32);
        string protectedValue = _protector.Protect(
            WebEncoders.Base64UrlEncode(token),
            _options.CookieLifetime);
        DateTimeOffset expires = _timeProvider.GetUtcNow().Add(_options.CookieLifetime);

        httpContext.Response.Cookies.Append(
            _options.CookieName,
            protectedValue,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = expires,
                MaxAge = _options.CookieLifetime,
                IsEssential = true
            });

        return token;
    }

    public bool TryGetToken(HttpContext httpContext, out byte[] token)
    {
        token = [];
        if (!httpContext.Request.Cookies.TryGetValue(
                _options.CookieName,
                out string? protectedValue) ||
            string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        try
        {
            byte[] decoded = WebEncoders.Base64UrlDecode(_protector.Unprotect(protectedValue));
            if (decoded.Length != 32)
            {
                return false;
            }

            token = decoded;
            return true;
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    public string ComputePollScopedHash(Guid pollId, ReadOnlySpan<byte> token)
    {
        byte[] input = new byte[HashDomain.Length + 16 + token.Length];
        HashDomain.CopyTo(input, 0);
        pollId.TryWriteBytes(input.AsSpan(HashDomain.Length, 16), bigEndian: true, out _);
        token.CopyTo(input.AsSpan(HashDomain.Length + 16));
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
