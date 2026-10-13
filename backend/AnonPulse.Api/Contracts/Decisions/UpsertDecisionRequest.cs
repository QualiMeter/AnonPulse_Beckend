using System.ComponentModel.DataAnnotations;

namespace AnonPulse.Api.Contracts.Decisions;

public static class DecisionContractLimits
{
    public const int MaximumTextLength = 5000;
}

public sealed record UpsertDecisionRequest
{
    private string? _text;

    public UpsertDecisionRequest()
    {
    }

    public UpsertDecisionRequest(string? text)
    {
        Text = text;
    }

    [MaxLength(DecisionContractLimits.MaximumTextLength)]
    public string? Text
    {
        get => _text;
        init => _text = value?.Trim();
    }
}
