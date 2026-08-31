using MafDemo.Core.Evals;

namespace MafDemo.Core.Tests;

public class EvalRunnerTests
{
    [Fact]
    public async Task Pass_counted_and_failures_listed()
    {
        Func<string, Task<string>> fake = q => Task.FromResult(q == "a" ? "VPN requires MFA" : "nope");
        var result = await EvalRunner.RunAsync(
            [new("a", "mfa"), new("b", "something")], fake);
        Assert.Equal((1, 2), (result.Passed, result.Total));
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task Match_is_case_insensitive()
    {
        Func<string, Task<string>> fake = _ => Task.FromResult("RMA must be filed within 30 DAYS.");
        var result = await EvalRunner.RunAsync([new("rma window?", "within 30 days")], fake);
        Assert.Equal(1, result.Passed);
        Assert.Empty(result.Failures);
    }
}