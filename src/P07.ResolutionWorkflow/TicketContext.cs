using MafDemo.Core.Domain;

namespace P07.ResolutionWorkflow;

/// <summary>
/// The single message type flowing along the resolution workflow's edges
/// (P07 plan: one shared record per graph — each executor refines it with
/// `with` and forwards). Optional fields start null and are filled by the
/// executor that owns that stage (Triage by triage, Diagnosis by diagnosis,
/// ProposedFix by diagnosis/escalation, OperatorNote by the HITL host).
/// </summary>
public record TicketContext(
    Guid TicketId,
    string Title,
    string Description,
    TicketPriority Priority,
    string Triage,
    string Diagnosis,
    string? ProposedFix,
    string? OperatorNote);

/// <summary>Typed request sent to the <c>FixApproval</c> RequestPort.</summary>
public record FixApprovalRequest(Guid TicketId, string ProposedFix);

/// <summary>Typed response the host returns through the FixApproval port.</summary>
public record ApprovalDecision(bool Approved, string Note);