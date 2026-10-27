using System.Security.Cryptography;
using System.Text;
using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Tests;

public sealed class PublicTestResultService(ApplicationDbContext dbContext)
    : IPublicTestResultService
{
    private static readonly byte[] MissingAttemptHash = SHA256.HashData(
        Encoding.UTF8.GetBytes("AnonPulse missing test attempt constant-time sentinel"));

    public async Task<PublicTestResultServiceResult> GetAsync(Guid pollId, Guid attemptId,
        string? accessSecret, CancellationToken cancellationToken = default)
    {
        if (!TryValidateSecret(accessSecret, out byte[] secretBytes))
        {
            return new PublicTestResultServiceResult(PublicTestResultOutcome.Invalid,
                ValidationErrors: new Dictionary<string, string[]>
                {
                    ["accessSecret"] = ["AccessSecret must be a 43-character base64url value."]
                });
        }

        var authorization = await dbContext.NamedAttempts.AsNoTracking()
            .Where(item => item.Id == attemptId && item.PollId == pollId &&
                           item.Poll.Kind == PollKind.Test)
            .Select(item => new { item.AccessSecretHash, item.IsFinal })
            .SingleOrDefaultAsync(cancellationToken);
        byte[] encodedSecret = Encoding.UTF8.GetBytes(accessSecret!);
        byte[] suppliedHash = SHA256.HashData(encodedSecret);
        byte[] storedHash = authorization is null
            ? MissingAttemptHash
            : Convert.FromHexString(authorization.AccessSecretHash);
        bool matches = CryptographicOperations.FixedTimeEquals(suppliedHash, storedHash);
        CryptographicOperations.ZeroMemory(secretBytes);
        CryptographicOperations.ZeroMemory(encodedSecret);
        CryptographicOperations.ZeroMemory(suppliedHash);
        if (authorization is null || !matches)
        {
            return new PublicTestResultServiceResult(PublicTestResultOutcome.NotFound);
        }
        if (!authorization.IsFinal)
        {
            return new PublicTestResultServiceResult(PublicTestResultOutcome.Succeeded,
                new PublicTestResultResponse(attemptId, false));
        }

        NamedAttempt attempt = await dbContext.NamedAttempts.AsNoTracking()
            .Where(item => item.Id == attemptId && item.PollId == pollId)
            .Include(item => item.Poll)
            .Include(item => item.Answers)
            .ThenInclude(answer => answer.Question)
            .Include(item => item.Answers)
            .ThenInclude(answer => answer.SelectedOptions)
            .ThenInclude(selection => selection.Option)
            .AsSplitQuery()
            .SingleAsync(cancellationToken);

        PublicTestResultAnswerResponse[] answers = attempt.Answers
            .OrderBy(answer => answer.QuestionId)
            .Select(answer => new PublicTestResultAnswerResponse(
                answer.Id,
                answer.QuestionId,
                answer.Question.Text,
                answer.SelectedOptions.Select(item => item.OptionId).Order().ToArray(),
                answer.SelectedOptions.OrderBy(item => item.Option.Position)
                    .ThenBy(item => item.OptionId)
                    .Select(item => new PublicTestResultSelectedOptionResponse(
                        item.OptionId, item.Option.Text))
                    .ToArray(),
                answer.Text,
                answer.Score!.Value,
                answer.MaximumScore))
            .ToArray();
        return new PublicTestResultServiceResult(PublicTestResultOutcome.Succeeded,
            new PublicTestResultResponse(
                attempt.Id,
                true,
                attempt.Poll.Title ?? "TEST",
                attempt.RespondentName,
                attempt.RespondentGroup,
                attempt.RawScore,
                attempt.MaximumScore,
                attempt.NormalizedScore,
                attempt.Poll.TestScale,
                answers));
    }

    private static bool TryValidateSecret(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != 43 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }
        try
        {
            bytes = WebEncoders.Base64UrlDecode(value);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
