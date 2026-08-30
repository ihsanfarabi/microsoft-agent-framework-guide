using MafDemo.Core.Domain;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using P06.TriageComposition;

namespace P07.ResolutionWorkflow.Executors;

/// <summary>
/// Triage node: LLM classifies the ticket into one category word (one of the
/// P07 local agents), the diagnosis node then routes to the matching P06
/// specialist.
/// </summary>
internal sealed class TriageExecutor(AIAgent triageAgent)
    : Executor<TicketContext>("Triage")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol)
    {
        return base.ConfigureProtocol(protocol).SendsMessageType(typeof(TicketContext));
    }

    public override async ValueTask HandleAsync(TicketContext ctx, IWorkflowContext context, CancellationToken ct = default)
    {
        var reply = await triageAgent.RunAsync(
            $"Classify in one word (network/software/hardware): {ctx.Title}: {ctx.Description}",
            cancellationToken: ct);
        var triaged = ctx with { Triage = reply.Text };
        Console.WriteLine($"[triage] ticket {triaged.TicketId}: {triaged.Triage}");
        await context.SendMessageAsync(triaged, cancellationToken: ct);
    }
}

/// <summary>
/// Diagnostic node: picks one of the P06 specialists by the triage word
/// (REUSE, NOT PORT — same <see cref="Specialists"/> factories P06 runs),
/// then fills the diagnosis and proposed fix on the context.
/// </summary>
internal sealed class DiagnosticExecutor(SpecialistTools tools)
    : Executor<TicketContext>("Diagnose")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol)
    {
        return base.ConfigureProtocol(protocol).SendsMessageType(typeof(TicketContext));
    }

    public override async ValueTask HandleAsync(TicketContext ctx, IWorkflowContext context, CancellationToken ct = default)
    {
        AIAgent specialist = Agents.SpecialistFor(ctx.Triage, tools);
        var reply = await specialist.RunAsync(
            $"Ticket: {ctx.Title}. {ctx.Description}\nDiagnose and propose a concise fix (1-3 sentences, action first).",
            cancellationToken: ct);
        var diagnosed = ctx with { Diagnosis = reply.Text, ProposedFix = reply.Text };
        Console.WriteLine($"[diagnose] ticket {diagnosed.TicketId}: {diagnosed.Diagnosis}");
        await context.SendMessageAsync(diagnosed, cancellationToken: ct);
    }
}

/// <summary>
/// Escalation node (Critical tickets only — conditional edge picks it): the
/// escalation-engineer agent refines the proposed fix before approval.
/// </summary>
internal sealed class EscalationExecutor(AIAgent escalationAgent)
    : Executor<TicketContext>("Escalation")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol)
    {
        return base.ConfigureProtocol(protocol).SendsMessageType(typeof(TicketContext));
    }

    public override async ValueTask HandleAsync(TicketContext ctx, IWorkflowContext context, CancellationToken ct = default)
    {
        var reply = await escalationAgent.RunAsync(
            $"Refine this fix for a Critical incident: {ctx.ProposedFix}",
            cancellationToken: ct);
        var escalated = ctx with { ProposedFix = reply.Text };
        Console.WriteLine($"[escalate] ticket {escalated.TicketId}: {escalated.ProposedFix}");
        await context.SendMessageAsync(escalated, cancellationToken: ct);
    }
}