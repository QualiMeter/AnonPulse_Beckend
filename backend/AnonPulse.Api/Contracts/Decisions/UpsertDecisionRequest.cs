using System.ComponentModel.DataAnnotations;

namespace AnonPulse.Api.Contracts.Decisions;

public static class DecisionContractLimits
{
    public const int MaximumTextLength = 5000;
}

public sealed record UpsertDecisionRequest(
    [param: MaxLength(DecisionContractLimits.MaximumTextLength)] string? Text);
