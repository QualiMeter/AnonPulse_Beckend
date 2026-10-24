namespace AnonPulse.Api.Contracts.Admin;

public sealed record AdminDashboardResponse(
    AdminUserSummaryResponse Users,
    AdminPollSummaryResponse Polls,
    int TotalParticipations,
    IReadOnlyList<DailyPollCreationResponse> PollCreationLast30Days);

public sealed record AdminUserSummaryResponse(
    int Total,
    int Active,
    int Blocked,
    IReadOnlyList<RoleCountResponse> ByRole);

public sealed record RoleCountResponse(string Role, int Count);

public sealed record AdminPollSummaryResponse(
    int Total,
    int Draft,
    int Open,
    int Closed,
    int WithPublishedDecisions);

public sealed record DailyPollCreationResponse(DateOnly Date, int Count);
