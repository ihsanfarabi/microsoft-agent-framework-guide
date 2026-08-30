using MafDemo.Core.Domain;

namespace P07.ResolutionWorkflow;

/// <summary>
/// Pure decision logic for the resolution workflow — kept free of agents and
/// stores so it is trivially unit-testable (the workflow executors in
/// <see cref="Executors"/> call into it; nothing here calls out).
/// </summary>
public static class ApprovalPolicy
{
    /// <summary>Only Critical tickets must detour through the escalation
    /// engineer before the fix goes to human approval.</summary>
    public static bool NeedsEscalation(TicketPriority priority) => priority == TicketPriority.Critical;

    /// <summary>Store note appended when the operator approves the proposed fix.</summary>
    public static string ResolutionNote(TicketContext ctx, ApprovalDecision decision) =>
        $"Resolved: {ctx.Diagnosis}. Fix applied: {ctx.ProposedFix}. Operator: {decision.Note}";

    /// <summary>Store note appended when the operator rejects — the ticket
    /// stays InProgress and the human's reasoning is preserved for rework.</summary>
    public static string RejectionNote(ApprovalDecision decision) =>
        $"Fix rejected by operator ({decision.Note}). Ticket remains in progress.";
}