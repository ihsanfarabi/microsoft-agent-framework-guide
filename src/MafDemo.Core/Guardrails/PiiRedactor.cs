using System.Text.RegularExpressions;

namespace MafDemo.Core.Guardrails;

public static partial class PiiRedactor
{
    [GeneratedRegex(@"EMP-\d{4,6}")]
    private static partial Regex EmployeeId();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+")]
    private static partial Regex Email();

    public static string Redact(string text) =>
        Email().Replace(EmployeeId().Replace(text, "[REDACTED-ID]"), "[REDACTED-EMAIL]");
}
