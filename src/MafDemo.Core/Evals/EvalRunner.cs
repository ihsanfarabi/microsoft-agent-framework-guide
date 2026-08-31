namespace MafDemo.Core.Evals;

/// <summary>
/// Table-driven eval runner: asks each case's question through the provided
/// answer function and checks a case-insensitive contains on the expected
/// fact. Failures echo the input so the log is directly actionable.
/// </summary>
public static class EvalRunner
{
    public static async Task<EvalResult> RunAsync(IEnumerable<EvalCase> cases, Func<string, Task<string>> answer)
    {
        var passed = 0;
        var failures = new List<string>();
        var evalCases = cases.ToArray();
        foreach (var c in evalCases)
        {
            var got = await answer(c.Input);
            if (got.Contains(c.ExpectedFact, StringComparison.OrdinalIgnoreCase))
                passed++;
            else
                failures.Add($"[{c.Input}] expected fact '{c.ExpectedFact}' but got: {got.Trim()}");
        }
        return new EvalResult(passed, evalCases.Length, failures.ToArray());
    }
}

/// <summary>Suite outcome: counts plus echoed failures for the failing inputs.</summary>
public record EvalResult(int Passed, int Total, string[] Failures);