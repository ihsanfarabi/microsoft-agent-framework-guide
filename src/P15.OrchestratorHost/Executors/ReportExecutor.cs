using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace P15.OrchestratorHost.Executors;

/// <summary>
/// Terminal node of the P15 graph: whatever answer the remote agents routed
/// to it arrives as a <c>List&lt;ChatMessage&gt;</c> — the shape every agent
/// node sends across its outgoing edges (the <c>ChatProtocolExecutor</c> base
/// of <c>AIAgentHostExecutor</c> sends the turn's response as a message list,
/// not an <c>AgentResponse</c>; verified by decompiling 1.19.0). It prints the
/// answer and yields a one-line completion as the workflow's final output.
/// </summary>
/// <remarks>
/// Agent nodes with the default <see cref="AIAgentHostOptions"/> also forward
/// the messages they received (reassigned to the user role) ahead of their own
/// response — hop-by-hop chatter that is NOT the answer. Only an
/// assistant-authored message is a remote result, so everything else is
/// ignored here. The trailing <see cref="TurnToken"/> an agent node sends
/// after its turn has no handler on this executor and is silently dropped by
/// the edge runner's CanHandle gate (verified in the 1.19.0 DirectEdgeRunner).
/// </remarks>
internal sealed class ReportExecutor : Executor
{
    public ReportExecutor() : base("Report") { }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) =>
        protocol
            .ConfigureRoutes(routes => routes.AddHandler<List<ChatMessage>>(OnResultsAsync))
            .YieldsOutputType(typeof(string));

    private async ValueTask OnResultsAsync(List<ChatMessage> messages, IWorkflowContext context, CancellationToken ct)
    {
        string? answer = messages.FirstOrDefault(m => m.Role == ChatRole.Assistant)?.Text;
        if (answer is null)
        {
            return; // forwarded inbound chatter (user role), not a remote result
        }

        Console.WriteLine($"[report] remote agent answer: {answer}");
        await context.YieldOutputAsync($"Report received the final answer ({answer.Length} chars).", ct);
    }
}
