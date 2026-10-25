using System.Text.RegularExpressions;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public interface IPersonalDataSanitizer
{
    string Sanitize(string text);
}

public sealed partial class PersonalDataSanitizer : IPersonalDataSanitizer
{
    [GeneratedRegex(@"(?<![\w.+-])[\w.+-]+@[\w-]+(?:\.[\w-]+)+(?![\w.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d[\d\s().-]{8,}\d)(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    public string Sanitize(string text)
    {
        string withoutEmails = EmailRegex().Replace(text, "[email удалён]");
        return PhoneRegex().Replace(withoutEmails, "[телефон удалён]");
    }
}
