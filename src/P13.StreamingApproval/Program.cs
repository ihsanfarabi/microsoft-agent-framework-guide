using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using P13.StreamingApproval;
using P13.StreamingApproval.Agents;

// P13.StreamingApproval — a self-hosted HTTP endpoint over the MAF approval
// round-trip. The in-process loop it HTTP-ifies lives in P08's
// DriveAsync/PromptApproval: the streaming run pauses on a sensitive tool
// call, the request is surfaced (there: console, here: SSE), and a second
// stimulus (there: keyboard input, here: a second HTTP request —
// POST /approvals/{id}) resumes the same session.
//
// One work/ area next to the binary holds the file-backed ticket store the
// tools mutate (P08 pattern). Runtime state, git-ignored.
var workRoot = Path.Combine(AppContext.BaseDirectory, "work");
Directory.CreateDirectory(workRoot);
var ticketStore = new DeletableTicketStore(
    Path.Combine(workRoot, "tickets.json"),
    Path.Combine(workRoot, "tickets-deleted.json"));
await DemoSeed.RunAsync(ticketStore);

var builder = WebApplication.CreateBuilder(args);

// The chat client is resolved through DI so the test host can swap in a
// scripted fake (offline SSE contract tests) — production resolves the shared
// OllamaChat factory with its OTel wrapper and config chain.
builder.Services.AddSingleton<IChatClient>(_ => OllamaChat.Create());
builder.Services.AddSingleton(ticketStore);
builder.Services.AddSingleton<PendingApprovals>();
// conversationId -> live session. In-memory (Task 1 scope): a restart drops
// sessions, and with them the ability to resume a paused conversation.
builder.Services.AddSingleton<ConversationSessions>();
builder.Services.AddSingleton<AIAgent>(sp =>
    TicketAgent.Build(
        sp.GetRequiredService<IChatClient>(),
        sp.GetRequiredService<DeletableTicketStore>()));

var app = builder.Build();

// POST /conversations/{id}/messages {"text": "..."} -> text/event-stream.
// The run is streamed frame by frame: assistant text as `data:` delta frames;
// when the agent surfaces a ToolApprovalRequestContent (a delete/escalate the
// operator must decide), the request + session are parked in PendingApprovals
// and an `event: approval` frame carries the requestId, tool name and
// arguments to the client. The response then ends — the conversation stays
// paused until POST /approvals/{id} answers the approval, resuming the
// stored session as SSE.
app.MapPost("/conversations/{id}/messages",
    async (string id, MessageRequest body, HttpContext http,
        AIAgent agent, PendingApprovals approvals, ConversationSessions sessions,
        CancellationToken ct) =>
    {
        var session = await sessions.GetOrCreateAsync(id, agent);

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await StreamSseFramesAsync(http.Response,
            SseWriter.EnumerateFrames(
                agent.RunStreamingAsync(new ChatMessage(ChatRole.User, body.Text), session,
                    cancellationToken: ct),
                id, session, approvals, ct),
            ct);
        return Results.Empty;
    });

// POST /approvals/{conversationId} {"requestId":"...","approved":true,
// "reason":"...","approveAlways":false} -> text/event-stream of the RESUMED
// turn. Atomically takes the parked request (one decision consumes one
// entry), answers it exactly as P08's PromptApproval does —
// CreateResponse(approved, reason), or CreateAlwaysApproveToolResponse for a
// standing always-approve rule — and re-runs the STORED session with the
// response riding in a user message. The resumed run streams through the same
// SseWriter shape as /messages, so if IT surfaces another
// ToolApprovalRequestContent the run pauses again: the new request is parked
// and a fresh `event: approval` frame ends this response. Loop-safe by
// construction — the loop is the client posting /approvals again, never
// server-side recursion.
app.MapPost("/approvals/{conversationId}",
        async (string conversationId, ApprovalVote vote, HttpContext http,
            AIAgent agent, PendingApprovals approvals, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(vote.RequestId))
                return Results.BadRequest(new { error = "requestId is required" });

            if (!approvals.TryTake(vote.RequestId, out var pending))
                return Results.NotFound(new
                {
                    error = "unknown requestId: already answered, or the process restarted " +
                            "(pending approvals are in-memory and do not survive a restart)",
                });

            if (!string.Equals(pending.ConversationId, conversationId, StringComparison.Ordinal))
            {
                // Put it back untouched (the store is keyed by requestId and
                // this take holds the only claim): resuming under another
                // conversation id would park any NEW requests from the resumed
                // run under the wrong conversation.
                approvals.Add(pending.ConversationId, pending.Request, pending.Session);
                return Results.Conflict(new
                {
                    error = $"requestId '{vote.RequestId}' belongs to conversation '{pending.ConversationId}'",
                });
            }

            // P08's PromptApproval mapping: approve once / always / decline —
            // the reason carries the operator's words back to the model.
            AIContent approvalResponse = vote.ApproveAlways
                ? pending.Request.CreateAlwaysApproveToolResponse(vote.Reason ?? "operator: always approve this tool")
                : pending.Request.CreateResponse(vote.Approved,
                    vote.Reason ?? (vote.Approved ? "operator approved" : "operator declined"));

            // The resume message is the paired approval response riding in a
            // user message (P08's DriveAsync resume shape): the harness's
            // ApprovalResponseBindingChatClient matches it to the session's
            // parked request by call id, executes (or refuses) the call, and
            // the model narrates the outcome as the resumed stream.
            var resumeMessage = new ChatMessage(ChatRole.User, [approvalResponse]);

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await StreamSseFramesAsync(http.Response,
                SseWriter.EnumerateFrames(
                    agent.RunStreamingAsync(resumeMessage, pending.Session, cancellationToken: ct),
                    conversationId, pending.Session, approvals, ct),
                ct);
            return Results.Empty;
        });

app.Run();

/// <summary>Writes SSE frames to the response as UTF-8 bytes — the one shape
/// both SSE endpoints stream through (a StreamWriter's AutoFlush performs a
/// synchronous flush, which Kestrel — and the test server even more strictly —
/// forbids on the response, so frames go straight to <c>Response.Body</c>).
/// A session with an unanswered approval request cannot take a stimulus that
/// does not answer it: the harness's FunctionInvokingChatClient refuses to
/// send pending ToolApprovalRequestContents back to the model without matching
/// responses (observed live — the exception fires before the first model call).
/// That contract violation surfaces as an SSE <c>event: error</c> frame
/// instead of a 500.</summary>
static async Task StreamSseFramesAsync(HttpResponse response,
    IAsyncEnumerable<string> frames, CancellationToken ct)
{
    try
    {
        await foreach (var frame in frames)
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("ToolApprovalRequestContent"))
    {
        var payload = JsonSerializer.Serialize(new
        {
            error = "approval-required: this conversation has an unanswered tool approval — resume it with the requestId from the approval event",
            detail = ex.Message,
        });
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes($"event: error\ndata: {payload}\n\n"), ct);
    }

    await response.Body.FlushAsync(ct);
}

/// <summary>Request body of the message endpoint. A missing/empty text is
/// rejected by the minimal-API validation below rather than reaching the
/// model.</summary>
public sealed record MessageRequest(string Text);

/// <summary>Request body of the approval endpoint: which parked request to
/// answer and how. <c>approved</c> is the one-vote decision (absent = false —
/// omitting a decision never grants one); <c>reason</c> carries the
/// operator's words to the model; <c>approveAlways</c> upgrades the vote to a
/// standing always-approve rule for that tool (P08's "a" answer), which
/// implies approval of this call.</summary>
public sealed record ApprovalVote(
    string RequestId, bool Approved = false, string? Reason = null, bool ApproveAlways = false);

/// <summary>Per-conversation live sessions: first message creates the
/// <see cref="AgentSession"/>, later messages continue it — the same session
/// object is what the approval flow parks and resumes.</summary>
public sealed class ConversationSessions
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

    public async Task<AgentSession> GetOrCreateAsync(string conversationId, AIAgent agent)
    {
        if (_sessions.TryGetValue(conversationId, out var session)) return session;
        var created = await agent.CreateSessionAsync();
        return _sessions.GetOrAdd(conversationId, created);
    }
}

/// <summary>SSE framing of the agent's streaming updates — pure, so the
/// contract tests run over a scripted client with no Kestrel (and the test
/// host can also exercise the endpoint itself, since this class carries no
/// HTTP state).</summary>
public static class SseWriter
{
    /// <summary>Streams the whole run as SSE frames: one
    /// <c>data: {"delta":...}</c> frame per update carrying text, one
    /// <c>event: approval</c> frame per pending approval request (stored
    /// first, so a client that reacts to the frame can immediately resume
    /// against it). The endpoint writes each frame to the response stream as
    /// it arrives; the pure <see cref="WriteAsync"/> form over a
    /// <see cref="TextWriter"/> is what the offline contract tests drive.</summary>
    public static async IAsyncEnumerable<string> EnumerateFrames(
        IAsyncEnumerable<AgentResponseUpdate> updates,
        string conversationId, AgentSession session, PendingApprovals approvals,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in updates.WithCancellation(ct))
            foreach (var frame in FramesFor(update, conversationId, session, approvals))
                yield return frame;
    }

    /// <summary>Writes the whole run as SSE frames to
    /// <paramref name="writer"/> — the pure, Kestrel-free form of
    /// <see cref="EnumerateFrames"/> used by the contract tests.</summary>
    public static async Task WriteAsync(TextWriter writer,
        IAsyncEnumerable<AgentResponseUpdate> updates,
        string conversationId, AgentSession session, PendingApprovals approvals,
        CancellationToken ct = default)
    {
        await foreach (var frame in EnumerateFrames(updates, conversationId, session, approvals, ct))
            await writer.WriteAsync(frame);
    }

    /// <summary>Maps one streaming update to the SSE frame(s) it emits —
    /// empty when the update carries neither text nor an approval request
    /// (tool results, role chunks and the like are not surfaced).</summary>
    public static IReadOnlyList<string> FramesFor(AgentResponseUpdate update,
        string conversationId, AgentSession session, PendingApprovals approvals)
    {
        var frames = new List<string>();

        // A single update can carry several approval requests at once (the
        // model bursts its destructive calls in one go — the P08
        // multi-request lesson), so every one is stored and framed.
        foreach (var request in update.Contents.OfType<ToolApprovalRequestContent>())
        {
            approvals.Add(conversationId, request, session);
            // The request's ToolCall is the model's original call — at
            // runtime a FunctionCallContent carrying the wire name and
            // arguments (10.9's ToolCallContent base type only exposes the
            // CallId).
            var call = request.ToolCall as FunctionCallContent;
            var payload = JsonSerializer.Serialize(new
            {
                requestId = request.RequestId,
                tool = call?.Name,
                args = call?.Arguments,
            });
            frames.Add($"event: approval\ndata: {payload}\n\n");
        }

        if (!string.IsNullOrEmpty(update.Text))
            frames.Add($"data: {JsonSerializer.Serialize(new { delta = update.Text })}\n\n");

        return frames;
    }
}

/// <summary>Seeds the demo ticket the live smoke test deletes, idempotent per
/// title (BacklogSeed's rule): a restart must not duplicate it, and an
/// already-seeded store must be left alone. Note the ticket id is a GUID
/// (FileTicketStore's key) — the live curl quotes whatever id the seed
/// created, not a made-up T-1001.</summary>
internal static class DemoSeed
{
    public static async Task RunAsync(DeletableTicketStore store)
    {
        const string title = "Password reset loop";
        if ((await store.ListAsync()).Any(t => t.Title == title)) return;
        await store.CreateAsync(title, "User locked out after repeated resets", TicketPriority.High);
    }
}

/// <summary>Makes the top-level Program visible to
/// WebApplicationFactory&lt;Program&gt; in the test project.</summary>
public partial class Program;
