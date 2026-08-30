using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI.Workflows;

namespace P07.ResolutionWorkflow.Executors;

/// <summary>
/// Approval node and resolution node in one executor: it asks for operator
/// sign-off through the <c>FixApproval</c> port (message flowing along the
/// edge to the port) and receives the operator's <see cref="ApprovalDecision"/>
/// back along the reverse edge (port → executor), then applies the decision
/// to the ticket store. So the executor must handle two message types — the
/// generic-typed <c>Executor&lt;T&gt;</c> on the other executors can't do
/// that, and the 1.19.0 package ships no <c>[MessageHandler]</c> source
/// generator (that came later — the executors doc was only updated 2026-08) —
/// so routes are registered imperatively by overriding
/// <see cref="Executor.ConfigureProtocol"/>.
///
/// The pending <see cref="TicketContext"/> rides out the pause in workflow
/// shared state (single fixed key — approvals are answered strictly in-flight,
/// so only one can be pending at a time) instead of an executor field: a field
/// would leak across the batch's three sequential runs, and shared state is
/// what survives checkpoints.
/// </summary>
internal sealed class ApprovalExecutor(ITicketStore store) : Executor("Approval")
{
    private const string PendingScope = "PendingApproval";
    private const string PendingKey = "ctx";

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol)
    {
        return protocol
            .ConfigureRoutes(routes => routes
                .AddHandler<TicketContext>(OnTicketAsync)
                .AddHandler<ApprovalDecision>(OnDecisionAsync))
            .SendsMessageType(typeof(FixApprovalRequest))
            .YieldsOutputType(typeof(string));
    }

    /// <summary>Entry from the diagnosis/escalation nodes: park the ticket in
    /// shared state and ask the operator to approve the proposed fix. The
    /// message flows along the edge to the <see cref="RequestPort"/>, which
    /// surfaces it to the host as <c>RequestInfoEvent</c>.</summary>
    private async ValueTask OnTicketAsync(TicketContext ctx, IWorkflowContext context, CancellationToken ct)
    {
        await context.QueueStateUpdateAsync(PendingKey, ctx, scopeName: PendingScope, cancellationToken: ct);
        await context.SendMessageAsync(new FixApprovalRequest(ctx.TicketId, ctx.ProposedFix!), ct);
    }

    /// <summary>Re-entry after the operator answers (the runtime routes the
    /// port's response back here): mutate the store and end the ticket's run
    /// with a summary line.</summary>
    private async ValueTask OnDecisionAsync(ApprovalDecision decision, IWorkflowContext context, CancellationToken ct)
    {
        var ctx = await context.ReadStateAsync<TicketContext>(PendingKey, scopeName: PendingScope, cancellationToken: ct)
            ?? throw new InvalidOperationException("Approval decision arrived with no pending ticket.");

        if (decision.Approved)
        {
            await store.UpdateStatusAsync(ctx.TicketId, TicketStatus.Resolved);
            await store.AddNoteAsync(ctx.TicketId, ApprovalPolicy.ResolutionNote(ctx, decision));
        }
        else
        {
            await store.UpdateStatusAsync(ctx.TicketId, TicketStatus.InProgress);
            await store.AddNoteAsync(ctx.TicketId, ApprovalPolicy.RejectionNote(decision));
        }

        await context.YieldOutputAsync(
            $"ticket {ctx.TicketId}: {(decision.Approved ? "resolved" : "rejected — in progress")}", ct);
    }
}