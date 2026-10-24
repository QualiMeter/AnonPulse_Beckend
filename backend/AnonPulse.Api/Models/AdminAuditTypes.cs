namespace AnonPulse.Api.Models;

public static class AdminAuditActionTypes
{
    public const string UserDeactivated = "user.deactivated";
    public const string UserReactivated = "user.reactivated";
    public const string UserDeleted = "user.deleted";
    public const string PollDeleted = "poll.deleted";
}

public static class AdminAuditTargetTypes
{
    public const string User = "user";
    public const string Poll = "poll";
}
