using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Services.Voting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnonPulse.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/public/polls")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PublicPollsController(
    IPublicVotingService votingService,
    IAnonymousVoterTokenService tokenService,
    IAnonymousVotingSessionRateLimiter sessionRateLimiter) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PublicPollResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        PublicPollResult result =
            await votingService.GetOpenPollAsync(id, cancellationToken);

        if (result.Outcome == PublicPollOutcome.Succeeded)
        {
            tokenService.GetOrCreateToken(HttpContext);
            return Ok(result.Poll);
        }

        return result.Outcome == PublicPollOutcome.NotFound
            ? NotFoundProblem("Poll was not found.")
            : ConflictProblem("Poll is not open for voting.");
    }

    [HttpPost("{id:guid}/votes")]
    [EnableRateLimiting("public-voting")]
    [TextAnswerRequestSizeLimit]
    public async Task<IActionResult> Submit(
        Guid id,
        SubmitVotesRequest request,
        CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("X-AnonPulse-Vote", out var header) ||
            header.Count != 1 ||
            !string.Equals(header[0], "1", StringComparison.Ordinal))
        {
            return ValidationError(new Dictionary<string, string[]>
            {
                ["X-AnonPulse-Vote"] = ["The X-AnonPulse-Vote: 1 header is required."]
            });
        }

        if (!tokenService.TryGetToken(HttpContext, out byte[] token))
        {
            return ValidationError(new Dictionary<string, string[]>
            {
                ["VotingSession"] =
                    ["A valid voting session is required. Open the poll before voting."]
            });
        }

        VotingRateLimitDecision rateLimit = sessionRateLimiter.TryAcquire(token);
        if (!rateLimit.IsAllowed)
        {
            int retryAfterSeconds = Math.Max(
                1,
                (int)Math.Ceiling(rateLimit.RetryAfter.TotalSeconds));
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many vote submissions.",
                detail: rateLimit.Reason);
        }

        string tokenHash = tokenService.ComputePollScopedHash(id, token);
        VoteSubmissionResult result = await votingService.SubmitAsync(
            id,
            request,
            tokenHash,
            cancellationToken);

        return result.Outcome switch
        {
            VoteSubmissionOutcome.Succeeded => NoContent(),
            VoteSubmissionOutcome.NotFound => NotFoundProblem("Poll was not found."),
            VoteSubmissionOutcome.Conflict => ConflictProblem(result.Error!),
            VoteSubmissionOutcome.Invalid => ValidationError(result.ValidationErrors!),
            _ => throw new InvalidOperationException("Unknown vote submission outcome.")
        };
    }

    private ObjectResult NotFoundProblem(string title) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: title);

    private ObjectResult ConflictProblem(string detail) => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Voting is not available.",
        detail: detail);

    private BadRequestObjectResult ValidationError(
        IReadOnlyDictionary<string, string[]> errors) =>
        BadRequest(new ValidationProblemDetails(
            errors.ToDictionary(item => item.Key, item => item.Value))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred."
        });
}
