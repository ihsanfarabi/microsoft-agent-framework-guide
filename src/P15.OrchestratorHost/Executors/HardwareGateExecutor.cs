using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace P15.OrchestratorHost.Executors;

/// <summary>
/// Routing node between the DiagnosisAgent and the InventoryAgent. The
/// conditional edge INTO this node already decided (a pure content predicate,
/// P07 style — no prints, no state) that the diagnosis flags NEEDS-HARDWARE;
/// this node then performs the chat-protocol handshake the remote inventory
/// node needs: forward the diagnosis result, then a <see cref="TurnToken"/> so
/// the hosted agent actually takes its turn (agent nodes only invoke their
/// agent when a TurnToken arrives — see TriageExecutor). On the software path
/// the edge condition drops the result and the trailing TurnToken, so the
/// InventoryAgent is never invoked: the skip is in the graph, not the model.
/// </summary>
internal sealed class HardwareGateExecutor : Executor
{
    public HardwareGateExecutor() : base("HardwareGate") { }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) =>
        protocol
            .ConfigureRoutes(routes => routes.AddHandler<List<ChatMessage>>(OnResultAsync))
            .SendsMessageType(typeof(List<ChatMessage>))
            .SendsMessageType(typeof(TurnToken));

    private async ValueTask OnResultAsync(List<ChatMessage> result, IWorkflowContext context, CancellationToken ct)
    {
        Console.WriteLine("[route] DiagnosisAgent (:5200) -> InventoryAgent (:5199): diagnosis flags NEEDS-HARDWARE");
        await context.SendMessageAsync(result, ct);
        await context.SendMessageAsync(new TurnToken(), ct);
    }
}
