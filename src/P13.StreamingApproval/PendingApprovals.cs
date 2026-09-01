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
/// In-memory store of approvals waiting for a decision. The SSE message
/// endpoint <see cref="Add"/>s a pending approval when the agent surfaces a
/// <see cref="ToolApprovalRequestContent"/>; the resume endpoint
/// <see cref="TryTake"/>s it by requestId and continues the stored session.
/// Process-local by design (the Task-3 restart contract): a restart drops
/// pending approvals — the parked turn dies with the process, the session
/// checkpoint keeps only the history, and the operator asks again.
/// </summary>
/// <remarks>
/// Burst guard (Task 3): one turn can surface SEVERAL
/// <see cref="ToolApprovalRequestContent"/>s — P08 observed glm-5.3 bursting
/// its destructive calls in a single update. Every one is parked (the
/// requestId index makes each take atomic), and a per-conversation queue
/// records the order they surfaced so a client can answer them in order.
/// Answering one never consumes another: each decision consumes exactly its
/// own requestId, so a burst of two needs two <c>POST /approvals</c> calls.
/// </remarks>
public sealed class PendingApprovals
{
    /// <summary>requestId -> parked entry. The take-side index: atomic remove
    /// is what makes one decision consume exactly one approval.</summary>
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();

    /// <summary>conversationId -> requestIds in surfacing order. Append-only;
    /// taken ids are filtered out on read, so <see cref="TryTake"/> needs no
    /// queue coordination (a re-park, e.g. the resume endpoint's
    /// conversation-mismatch guard, may append a duplicate that
    /// <see cref="PendingRequestIds"/> dedups away).</summary>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _byConversation = new();

    /// <summary>How many approvals are currently waiting — diagnostics and
    /// the contract tests' way to assert nothing (or exactly one) is parked.</summary>
    public int Count => _pending.Count;

    /// <summary>Records a pending approval keyed by its request id and files
    /// it at the back of its conversation's queue. Returns false (and stores
    /// nothing) if the same requestId is already pending — a duplicated frame
    /// must not overwrite the original request instance, which is what the
    /// resume path needs to answer.</summary>
    public bool Add(string conversationId, ToolApprovalRequestContent request, AgentSession session)
    {
        if (!_pending.TryAdd(request.RequestId, new PendingApproval(conversationId, request, session)))
            return false;

        _byConversation.GetOrAdd(conversationId, _ => new ConcurrentQueue<string>())
            .Enqueue(request.RequestId);
        return true;
    }

    /// <summary>Atomically removes and returns the pending approval for
    /// <paramref name="requestId"/> — one decision consumes one entry, so a
    /// double-posted resume cannot answer the same call twice, and the OTHER
    /// requests of the same burst turn stay parked untouched.</summary>
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

    /// <summary>The still-pending requestIds of one conversation, oldest
    /// first — the burst's surfacing order, which the client should answer
    /// in. Consumed ids are gone; duplicates from a re-park are collapsed.</summary>
    public IReadOnlyList<string> PendingRequestIds(string conversationId) =>
        _byConversation.TryGetValue(conversationId, out var queue)
            ? queue.Where(id => _pending.ContainsKey(id)).Distinct().ToList()
            : [];
}
