using System.Security.Claims;
using AnonPulse.Api.Contracts.KnowledgeGrading;
using AnonPulse.Api.Models;
using AnonPulse.Api.Security;
using AnonPulse.Api.Services.KnowledgeGrading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Authorize(Roles = RoleNames.Teacher + "," + RoleNames.Admin)]
[Route("api/polls/{pollId:guid}/questions/{questionId:guid}/knowledge-grading")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class KnowledgeGradingController(IKnowledgeGradingService service) : ControllerBase
{
    [HttpPost]
    [ValidateCsrfToken]
    public async Task<ActionResult<KnowledgeGradingJobResponse>> Start(Guid pollId,
        Guid questionId, StartKnowledgeGradingRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        KnowledgeGradingServiceResult result = await service.StartAsync(actorId,
            User.IsInRole(RoleNames.Admin), pollId, questionId,
            request.ExternalProcessingConfirmed, cancellationToken);
        return result.Outcome == KnowledgeGradingOutcome.Succeeded
            ? Accepted(result.Job) : MapError<KnowledgeGradingJobResponse>(result);
    }

    [HttpGet]
    public async Task<ActionResult<KnowledgeGradingJobResponse>> Get(Guid pollId,
        Guid questionId, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        KnowledgeGradingServiceResult result = await service.GetAsync(actorId,
            User.IsInRole(RoleNames.Admin), pollId, questionId, cancellationToken);
        return result.Outcome == KnowledgeGradingOutcome.Succeeded
            ? Ok(result.Job) : MapError<KnowledgeGradingJobResponse>(result);
    }

    [HttpGet("results")]
    public async Task<ActionResult<KnowledgeGradingResultsResponse>> GetResults(Guid pollId,
        Guid questionId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Page must be positive and pageSize must be between 1 and 100.",
                Extensions = { ["code"] = "invalid_pagination" }
            });
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        KnowledgeGradingServiceResult result = await service.GetResultsAsync(actorId,
            User.IsInRole(RoleNames.Admin), pollId, questionId, page, pageSize,
            cancellationToken);
        return result.Outcome == KnowledgeGradingOutcome.Succeeded
            ? Ok(result.Results) : MapError<KnowledgeGradingResultsResponse>(result);
    }

    private ActionResult<T> MapError<T>(KnowledgeGradingServiceResult result)
    {
        int status = result.Outcome switch
        {
            KnowledgeGradingOutcome.NotFound => StatusCodes.Status404NotFound,
            KnowledgeGradingOutcome.Forbidden => StatusCodes.Status403Forbidden,
            KnowledgeGradingOutcome.Conflict => StatusCodes.Status409Conflict,
            KnowledgeGradingOutcome.Disabled => StatusCodes.Status503ServiceUnavailable,
            KnowledgeGradingOutcome.TooLarge => StatusCodes.Status413PayloadTooLarge,
            KnowledgeGradingOutcome.RateLimited => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status400BadRequest
        };
        if (result.RetryAfterSeconds is int retryAfter)
            Response.Headers.RetryAfter = retryAfter.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(status, new ProblemDetails
        {
            Status = status,
            Title = result.Error ?? "Knowledge grading request was rejected.",
            Extensions = { ["code"] = result.ErrorCode }
        });
    }

    private bool TryGetActorId(out Guid actorId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);
}
