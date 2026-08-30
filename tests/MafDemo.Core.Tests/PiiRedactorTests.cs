using MafDemo.Core.Guardrails;

public class PiiRedactorTests
{
    [Theory]
    [InlineData("Employee EMP-44555 says hi", "Employee [REDACTED-ID] says hi")]
    [InlineData("EMP-123456 and EMP-1234", "[REDACTED-ID] and [REDACTED-ID]")]
    [InlineData("mail me at jane.doe@contoso.com please", "mail me at [REDACTED-EMAIL] please")]
    [InlineData("EMP-12 is too short", "EMP-12 is too short")]
    [InlineData("clean text", "clean text")]
    public void Redact_patterns_and_leave_clean_text(string input, string expected)
        => Assert.Equal(expected, PiiRedactor.Redact(input));
}
