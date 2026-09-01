using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P13.StreamingApproval;

/// <summary>
/// One tool call held for a human decision: the approval request the agent
/// surfaced, the session it belongs to (so the resume endpoint can continue
/// the same conversation — the session carries the chat history and the
/// approval binding state), and the conversation that produced it.
/// </summary>
/// <param name="ConversationId">The conversation whose run asked for the
/// approval.</param>
/// <param name="Request">The <see cref="ToolApprovalRequestContent"/> as the
/// agent surfaced it; <c>CreateResponse</c> on this exact instance is what the
/// resume path sends back, matched by call id by the harness's
/// ApprovalResponseBindingChatClient.</param>
/// <param name="Session">The session of the paused run.</param>
public sealed record PendingApproval(
    string ConversationId,
    ToolApprovalRequestContent Request,
    AgentSession Session)
{
    /// <summary>The correlation id for the approval round-trip: the id the
    /// SSE <c>approval</c> frame carries and the HTTP resume call quotes.</summary>
    public string RequestId => Request.RequestId;
}

/// <summary>
/// In-memory store of approvals waiting for a decision. Task-1 scope: the
/// SSE message endpoint <see cref="Add"/>s a pending approval when the agent
/// surfaces a <see cref="ToolApprovalRequestContent"/>; the resume endpoint
/// (a later task) <see cref="TryTake"/>s it by requestId and continues the
/// stored session. Process-local by design — a restart drops pending
/// approvals, the same way the in-process P08 console harness would.
/// </summary>
public sealed class PendingApprovals
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();

    /// <summary>How many approvals are currently waiting — diagnostics and
    /// the contract tests' way to assert nothing (or exactly one) is parked.</summary>
    public int Count => _pending.Count;

    /// <summary>Records a pending approval keyed by its request id. Returns
    /// false (and stores nothing) if the same requestId is already pending —
    /// a duplicated frame must not overwrite the original request instance,
    /// which is what the resume path needs to answer.</summary>
    public bool Add(string conversationId, ToolApprovalRequestContent request, AgentSession session) =>
        _pending.TryAdd(request.RequestId, new PendingApproval(conversationId, request, session));

    /// <summary>Atomically removes and returns the pending approval for
    /// <paramref name="requestId"/> — one decision consumes one entry, so a
    /// double-posted resume cannot answer the same call twice.</summary>
    public bool TryTake(string requestId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PendingApproval? approval)
    {
        approval = null;
        if (_pending.TryRemove(requestId, out var taken))
        {
            approval = taken;
            return true;
        }

        return false;
    }
}
