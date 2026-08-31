using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P08.HarnessAgent;

/// <summary>
/// Approval boundary for the overnight batch: everything the agent may do to
/// consult and enrich tickets is read-only (create/list/get/note) and passes
/// the gate automatically; <c>close_ticket</c> is the one irreversible action,
/// so it falls through to a human prompt instead. The rule joins the harness's
/// auto-approval list alongside the MAF file-access rule — a <see langword="false"/>
/// return is not a rejection, it just means "next rule / human decides".
/// </summary>
public static class ApprovalPolicy
{
    /// <summary>Wire names that never require a human: every ticket tool
    /// except <c>close_ticket</c>.</summary>
    private static readonly HashSet<string> ReadOnly =
    [
        "create_ticket",
        "list_tickets",
        "get_ticket",
        "add_note",
    ];

    /// <summary>True when the call's tool is read-only and may run unattended;
    /// false for anything else (notably <c>close_ticket</c>), which then needs
    /// an explicit approval.</summary>
    public static ValueTask<bool> ShouldAutoApprove(FunctionCallContent call) =>
        ValueTask.FromResult(ReadOnly.Contains(call.Name));

    /// <summary>
    /// Harness-facing overload: <c>ToolApprovalAgentOptions.AutoApprovalRules</c>
    /// takes <c>Func&lt;ToolAutoApprovalRuleContext, ValueTask&lt;bool&gt;&gt;</c>
    /// (verified against Microsoft.Agents.AI 1.19.0), so the rule adapts the
    /// pure <see cref="ShouldAutoApprove(FunctionCallContent)"/> decision to the
    /// run context the harness hands it.
    /// </summary>
    public static ValueTask<bool> ShouldAutoApprove(ToolAutoApprovalRuleContext context) =>
        ShouldAutoApprove(context.FunctionCallContent);
}