using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace P15.OrchestratorHost.Executors;

/// <summary>
/// Entry node of the P15 graph: takes the raw ticket text and hands it to the
/// remote DiagnosisAgent node. The remote agents are hosted in the graph as
/// <see cref="AIAgentBinding"/>s, and an agent node implements the *chat
/// protocol* (verified by decompiling
/// <c>Specialized.AIAgentHostExecutor</c> in Microsoft.Agents.AI.Workflows
/// 1.19.0): it accumulates <see cref="ChatMessage"/>s and only invokes the
/// hosted agent when a <see cref="TurnToken"/> arrives. So the edge into an
/// agent node must carry the message AND then the turn token — a string alone
/// would sit in the node's pending-message state forever.
/// </summary>
internal sealed class TriageExecutor : Executor
{
    public TriageExecutor() : base("Triage") { }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) =>
        protocol
            .ConfigureRoutes(routes => routes.AddHandler<string>(IngestAsync))
            .SendsMessageType(typeof(ChatMessage))
            .SendsMessageType(typeof(TurnToken));

    private async ValueTask IngestAsync(string ticket, IWorkflowContext context, CancellationToken ct)
    {
        Console.WriteLine("[hop] Triage (local process) -> DiagnosisAgent (localhost:5200, A2A message:send)");
        await context.SendMessageAsync(new ChatMessage(ChatRole.User, ticket), ct);
        await context.SendMessageAsync(new TurnToken(), ct);
    }
}
