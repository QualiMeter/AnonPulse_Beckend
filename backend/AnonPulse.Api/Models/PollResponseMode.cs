namespace AnonPulse.Api.Models;

public enum PollResponseMode
{
    Anonymous,
    Identified
}

public static class PollResponseModeExtensions
{
    public static string ToApiValue(this PollResponseMode mode) => mode switch
    {
        PollResponseMode.Anonymous => "anonymous",
        PollResponseMode.Identified => "identified",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}
