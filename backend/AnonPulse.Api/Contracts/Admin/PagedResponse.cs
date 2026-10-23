namespace AnonPulse.Api.Contracts.Admin;

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public static class AdminPagination
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
}
