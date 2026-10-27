using System.Text.Json.Serialization;

namespace AnonPulse.Api.Contracts.PublicPolls;

public sealed record GetTestResultRequest(string? AccessSecret);

public sealed record PublicTestResultResponse(
    Guid AttemptId,
    bool IsFinal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TestTitle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RespondentName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RespondentGroup = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? RawScore = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? MaximumScore = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? NormalizedScore = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TestScale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PublicTestResultAnswerResponse>? Answers = null);

public sealed record PublicTestResultAnswerResponse(
    Guid AnswerId,
    Guid QuestionId,
    string QuestionText,
    IReadOnlyList<Guid> SelectedOptionIds,
    IReadOnlyList<PublicTestResultSelectedOptionResponse> SelectedOptions,
    string? Text,
    decimal Score,
    decimal MaximumScore);

public sealed record PublicTestResultSelectedOptionResponse(Guid Id, string Text);
