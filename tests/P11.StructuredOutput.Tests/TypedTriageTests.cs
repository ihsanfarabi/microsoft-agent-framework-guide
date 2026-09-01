using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using Microsoft.Agents.AI;
using P11.StructuredOutput;

namespace P11.StructuredOutput.Tests;

public class TypedTriageTests
{
    [Fact]
    public void ProbeJson_accepts_valid_decision()
    {
        var probe = TypedTriage.ProbeJson(
            """{"Category":"Hardware","Priority":2,"Summary":"dead battery"}""");
        Assert.True(probe.Ok);
    }

    [Fact]
    public void ProbeJson_rejects_malformed_and_out_of_range()
    {
        Assert.False(TypedTriage.ProbeJson("not json at all").Ok);
        Assert.False(TypedTriage.ProbeJson(
            """{"Category":"Hardware","Priority":99,"Summary":"odd"}""").Ok);
    }

    [Fact]
    public async Task RunAsync_returns_typed_decision()
    {
        if (Environment.GetEnvironmentVariable("RUN_EVALS") != "1") return;

        var agent = TypedTriage.TypedTriageAgent(OllamaChat.Create());
        AgentResponse<TriageDecision> response =
            await agent.RunAsync<TriageDecision>("Laptop won't boot, deadline tomorrow",
                serializerOptions: TypedTriage.JsonOptions);

        Assert.NotNull(response.Result);
        Assert.True(Enum.IsDefined(response.Result.Category)); // any valid category
        Assert.True(Enum.IsDefined(response.Result.Priority));
        Assert.False(string.IsNullOrWhiteSpace(response.Result.Summary));
    }
}