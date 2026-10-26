namespace AnonPulse.Api.Models;

public enum PollKind
{
    Survey,
    Test
}

public static class PollKindExtensions
{
    public static string ToApiValue(this PollKind kind) => kind switch
    {
        PollKind.Survey => "survey",
        PollKind.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
