using System.Security.Claims;
using AnonPulse.Api.Contracts.FeedbackAnalysis;
using AnonPulse.Api.Models;
using AnonPulse.Api.Security;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Authorize(Roles = RoleNames.Teacher + "," + RoleNames.Admin)]
[Route("api/polls/{pollId:guid}/questions/{questionId:guid}/feedback-analysis")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FeedbackAnalysisController(IFeedbackAnalysisService service) : ControllerBase
{
    [HttpPost]
    [ValidateCsrfToken]
    public async Task<ActionResult<FeedbackAnalysisResponse>> Start(
        Guid pollId, Guid questionId, StartFeedbackAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        FeedbackAnalysisServiceResult result = await service.StartAsync(actorId,
            User.IsInRole(RoleNames.Admin), pollId, questionId,
            request.ExternalProcessingConfirmed, cancellationToken);
        if (result.Outcome == FeedbackAnalysisOutcome.Succeeded)
            return Accepted(result.Analysis);
        return MapError(result);
    }

    [HttpGet]
    public async Task<ActionResult<FeedbackAnalysisResponse>> Get(
        Guid pollId, Guid questionId, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        FeedbackAnalysisServiceResult result = await service.GetAsync(actorId,
            User.IsInRole(RoleNames.Admin), pollId, questionId, cancellationToken);
        return result.Outcome == FeedbackAnalysisOutcome.Succeeded
            ? Ok(result.Analysis) : MapError(result);
    }

    private ActionResult<FeedbackAnalysisResponse> MapError(FeedbackAnalysisServiceResult result)
    {
        int status = result.Outcome switch
        {
            FeedbackAnalysisOutcome.NotFound => StatusCodes.Status404NotFound,
            FeedbackAnalysisOutcome.Forbidden => StatusCodes.Status403Forbidden,
            FeedbackAnalysisOutcome.Conflict => StatusCodes.Status409Conflict,
            FeedbackAnalysisOutcome.Disabled => StatusCodes.Status503ServiceUnavailable,
            FeedbackAnalysisOutcome.TooLarge => StatusCodes.Status413PayloadTooLarge,
            FeedbackAnalysisOutcome.RateLimited => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status400BadRequest
        };
        if (result.RetryAfterSeconds is int retryAfterSeconds)
            Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(status, new ProblemDetails
        {
            Status = status,
            Title = result.Error ?? "Feedback analysis request was rejected.",
            Extensions = { ["code"] = result.ErrorCode }
        });
    }

    private bool TryGetActorId(out Guid actorId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);
}
