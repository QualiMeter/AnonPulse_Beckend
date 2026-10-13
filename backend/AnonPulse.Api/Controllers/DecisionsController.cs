using System.Security.Claims;
using AnonPulse.Api.Contracts.Decisions;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Decisions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/polls/{pollId:guid}/decision")]
public sealed class DecisionsController(IDecisionService decisionService)
    : ControllerBase
{
    [HttpPut]
    [Authorize(Roles = RoleNames.Teacher + "," + RoleNames.Admin)]
    public async Task<ActionResult<DecisionResponse>> Upsert(
        Guid pollId,
        UpsertDecisionRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                out Guid actorId))
        {
            return Unauthorized();
        }

        DecisionUpsertResult result = await decisionService.UpsertAsync(
            actorId,
            User.IsInRole(RoleNames.Admin),
            pollId,
            request,
            cancellationToken);

        return result.Outcome switch
        {
            DecisionUpsertOutcome.Succeeded => Ok(result.Decision),
            DecisionUpsertOutcome.Invalid => BadRequest(
                new ValidationProblemDetails(
                    result.ValidationErrors!.ToDictionary(
                        item => item.Key,
                        item => item.Value))
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred."
                }),
            DecisionUpsertOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Poll was not found."
            }),
            DecisionUpsertOutcome.Conflict => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Decision cannot be published.",
                Detail = result.Error
            }),
            _ => throw new InvalidOperationException(
                $"Unknown decision upsert outcome: {result.Outcome}.")
        };
    }

    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<DecisionResponse>> Get(
        Guid pollId,
        CancellationToken cancellationToken)
    {
        PublishedDecisionResult result =
            await decisionService.GetPublishedAsync(pollId, cancellationToken);

        return result.Outcome == PublishedDecisionOutcome.Succeeded
            ? Ok(result.Decision)
            : NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Published decision was not found."
            });
    }
}
