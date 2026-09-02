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
// conversationId -> live session, checkpointed to work/sessions/{id}.json
// after every stream end and rehydrated on demand (P08's per-run checkpoint
// pattern). A restart therefore keeps the conversation HISTORY; what it
// drops is PendingApprovals — the parked approvals are in-memory only, so a
// paused turn dies with the process (the /approvals error frame documents
// that recovery: ask again).
builder.Services.AddSingleton(_ =>
    new ConversationSessions(Path.Combine(workRoot, "sessions")));
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
        // Minimal validation (review fix): a missing body is already rejected
        // with 400 by model binding, but a present-yet-blank text would
        // otherwise reach the model — reject it before any session or run.
        if (string.IsNullOrWhiteSpace(body.Text))
        {
            return Results.BadRequest(new { error = "empty_text", detail = "body.text is required" });
        }

        // Per-conversation run gate: held for the whole run (session
        // get-or-create through checkpoint) so two requests on one
        // conversation cannot interleave their runs on the shared session.
        await using var _ = await sessions.AcquireAsync(id);
        var session = await sessions.GetOrCreateAsync(id, agent);

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        try
        {
            await StreamSseFramesAsync(http.Response,
                SseWriter.EnumerateFrames(
                    agent.RunStreamingAsync(new ChatMessage(ChatRole.User, body.Text), session,
                        cancellationToken: ct),
                    id, session, approvals, ct),
                ct);
            // P08's checkpoint discipline: the moment the stream ends, the
            // session — history and any parked approval state — is serialized to
            // disk (atomic temp-file move), so a later message (or a process
            // restart) continues the conversation instead of starting over.
            // Failures are swallowed with a console line (P08's fail-soft rule):
            // a lost checkpoint must never crash the response after its work.
            await sessions.CheckpointAsync(id, session, agent, ct);
        }
        catch (OperationCanceledException)
        {
            // Client disconnect or shutdown mid-run: nothing to deliver to — the
            // checkpoint is deliberately skipped (the next successful run's
            // checkpoint supersedes the stale file).
            throw;
        }
        catch (Exception ex)
        {
            // Model down, tool crash, anything else mid-stream: the response has
            // already started 200 text/event-stream, so the only deliverable
            // failure shape is an SSE error frame — never a dropped connection
            // (the endpoint's own contract: every terminal outcome is an error
            // frame). The checkpoint is deliberately NOT taken when the run
            // threw: disk keeps the last known-good turn, same rule as
            // /approvals.
            await WriteSseErrorAsync(http.Response, new
            {
                error = "run-failed",
                detail = ex.Message,
                recovery = "re-send the message to retry the turn",
            }, ct);
        }

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
// server-side recursion. A burst turn (several parked requests) is answered
// by posting once per requestId, in the order they surfaced.
app.MapPost("/approvals/{conversationId}",
        async (string conversationId, ApprovalVote vote, HttpContext http,
            AIAgent agent, PendingApprovals approvals, ConversationSessions sessions,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(vote.RequestId))
                return Results.BadRequest(new { error = "requestId is required" });

            if (!approvals.TryTake(vote.RequestId, out var pending))
            {
                // Restart contract (Task 3): pending approvals are in-memory
                // while the session checkpoints on disk — so after a restart
                // the session history survives but the parked turn died with
                // the process. This is surfaced as an SSE error frame (never
                // a 500), telling the client the documented recovery: ask
                // again — re-send the message and approve the fresh request.
                await WriteSseErrorAsync(http.Response, new
                {
                    error = "unknown-request-id",
                    detail = $"no pending approval '{vote.RequestId}': already answered, or the " +
                             "process restarted (pending approvals are in-memory and do not survive a restart)",
                    recovery = "the parked turn died with the process — ask again: re-send the message " +
                               "to /conversations/{id}/messages and approve the fresh request",
                }, ct);
                return Results.Empty;
            }

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

            // Same per-conversation gate as /messages — taken only after the
            // take + conversation check above, so a bad or foreign request
            // bounces without holding the gate.
            await using var _ = await sessions.AcquireAsync(conversationId);
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            try
            {
                await StreamSseFramesAsync(http.Response,
                    SseWriter.EnumerateFrames(
                        agent.RunStreamingAsync(resumeMessage, pending.Session, cancellationToken: ct),
                        conversationId, pending.Session, approvals, ct),
                    ct);
                // Stream ended (deltas, another parked approval, or the
                // handled approval-required error frame) — checkpoint the
                // session, same fail-soft rule as /messages. A checkpoint is
                // deliberately NOT taken when the run THREW: the disk keeps
                // the last known-good turn, and the in-memory session is what
                // a re-park retry continues from.
                await sessions.CheckpointAsync(conversationId, pending.Session, agent, ct);
            }
            catch (OperationCanceledException)
            {
                // Client disconnect or shutdown mid-resume: re-park so the
                // decision is not orphaned — the T2-review minor (TryTake was
                // not re-parked when the resume threw). Hazard (P08's NOTES
                // resume note): the call may or may not have EXECUTED before
                // the cancellation — a re-answer resumes the session from its
                // current state, and if the call already ran, the harness
                // finds no matching pending call and surfaces an error rather
                // than double-executing.
                approvals.Add(pending.ConversationId, pending.Request, pending.Session);
                throw;
            }
            catch (Exception ex)
            {
                // Same re-park for a failed resume: the approval returns to
                // the store so the operator can answer it again, and the
                // failure streams out as an error frame instead of a 500.
                approvals.Add(pending.ConversationId, pending.Request, pending.Session);
                await WriteSseErrorAsync(http.Response, new
                {
                    error = "resume-failed",
                    detail = ex.Message,
                    recovery = "the approval was re-parked — POST /approvals again, or re-send the message",
                }, ct);
            }

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
            error = "approval-required: this conversation has an unanswered tool approval — resume it " +
                    "with the requestId from the approval event (if the process restarted, that requestId " +
                    "is gone: start a new conversation)",
            detail = ex.Message,
        });
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes($"event: error\ndata: {payload}\n\n"), ct);
    }

    await response.Body.FlushAsync(ct);
}

/// <summary>Writes one <c>event: error</c> SSE frame — the shape every
/// terminal-but-not-exceptional outcome takes (unanswered approval, unknown
/// requestId after a restart, failed resume). Never a 500: the client gets a
/// machine-readable error name plus a human-readable recovery.</summary>
static async Task WriteSseErrorAsync(HttpResponse response, object payload,
    CancellationToken ct)
{
    response.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    await response.Body.WriteAsync(
        Encoding.UTF8.GetBytes($"event: error\ndata: {JsonSerializer.Serialize(payload)}\n\n"), ct);
    await response.Body.FlushAsync(ct);
}

/// <summary>Request body of the message endpoint. A missing body is rejected
/// by model binding; a present-but-blank <see cref="Text"/> is rejected with
/// 400 by the handler's check — neither reaches the model.</summary>
public sealed record MessageRequest(string Text);

/// <summary>Request body of the approval endpoint: which parked request to
/// answer and how. <c>approved</c> is the one-vote decision (absent = false —
/// omitting a decision never grants one); <c>reason</c> carries the
/// operator's words to the model; <c>approveAlways</c> upgrades the vote to a
/// standing always-approve rule for that tool (P08's "a" answer), which
/// implies approval of this call.</summary>
public sealed record ApprovalVote(
    string RequestId, bool Approved = false, string? Reason = null, bool ApproveAlways = false);

/// <summary>Per-conversation live sessions with disk checkpoints (Task 3,
/// P08's session-state pattern): the first message of a conversation
/// rehydrates <c>sessions/{id}.json</c> if one exists (deserialize, then
/// verify the round-trip by re-serializing before trusting it), later
/// messages continue the in-memory session, and <see cref="CheckpointAsync"/>
/// — called after every stream end by both endpoints — serializes the
/// session back (atomic temp-file move, so a kill during the write cannot
/// leave a torn file). Checkpoint failures other than cancellation are
/// logged and swallowed (P08's rule): the next successful checkpoint
/// supersedes the stale file, and the response must not crash after its
/// work is done. Per-conversation runs serialize on <see cref="AcquireAsync"/>:
/// both endpoints hold the gate for the whole run (session get-or-create
/// through checkpoint), because two runs on the SAME conversation share one
/// AgentSession and one checkpoint tmp file — interleaving them duplicates
/// history and torn-writes the checkpoint. Cross-conversation runs
/// parallelize.</summary>
public sealed class ConversationSessions(string directory)
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>Per-conversation run gate: both endpoints hold it for the whole
    /// run (session get-or-create through checkpoint), so two runs on the SAME
    /// conversation serialize. Released via DisposeAsync on the returned
    /// releaser — <c>await using</c> is the intended usage.</summary>
    public async Task<SemaphoreReleaser> AcquireAsync(string conversationId)
    {
        var gate = _gates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        return new SemaphoreReleaser(gate);
    }

    /// <summary>Releases one conversation gate on dispose.</summary>
    public sealed record SemaphoreReleaser(SemaphoreSlim Gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The deserialized sessions' source documents, held alive for
    /// the process lifetime — P08's lesson: deserialized session state may
    /// hold <see cref="System.Text.Json.JsonElement"/>s backed by the parsed
    /// document, so it must outlive the <see cref="AgentSession"/> built from
    /// it. A server holds sessions indefinitely, so the documents live with
    /// them.</summary>
    private readonly ConcurrentDictionary<string, System.Text.Json.JsonDocument> _sourceDocuments = new();

    public async Task<AgentSession> GetOrCreateAsync(string conversationId, AIAgent agent)
    {
        if (_sessions.TryGetValue(conversationId, out var session)) return session;
        var restored = await TryLoadAsync(conversationId, agent);
        var created = restored ?? await agent.CreateSessionAsync();
        return _sessions.GetOrAdd(conversationId, created);
    }

    /// <summary>Serializes the session to
    /// <c>sessions/{conversationId}.json</c> — P08's <c>CheckpointAsync</c>
    /// verbatim apart from the per-conversation path.</summary>
    public async Task CheckpointAsync(string conversationId, AgentSession session, AIAgent agent,
        CancellationToken ct = default)
    {
        var path = PathFor(conversationId);
        try
        {
            Directory.CreateDirectory(directory);
            var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, serialized.GetRawText(), ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[checkpoint] FAILED to save session state for '{conversationId}': {ex.Message}");
        }
    }

    /// <summary>Loads a previously checkpointed session, or null when there
    /// is no file (fresh conversation) or the file is unusable — a corrupt
    /// file is quarantined (moved aside, never overwritten by the next save)
    /// and the conversation starts fresh. Unlike P08's CLI — which bricks
    /// startup rather than risk redoing finished work — failing soft to a
    /// fresh session is safe here: every destructive call is approval-gated
    /// again, so a lost history cannot cause a lost decision, only a lost
    /// memory.</summary>
    private async Task<AgentSession?> TryLoadAsync(string conversationId, AIAgent agent)
    {
        var path = PathFor(conversationId);
        if (!File.Exists(path)) return null;

        try
        {
            var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var session = await agent.DeserializeSessionAsync(saved.RootElement);
            // Round-trip verification: the restored session must survive its
            // own save format — a session that cannot be serialized back
            // would checkpoint garbage over the good file on the next turn.
            _ = System.Text.Json.JsonDocument.Parse(
                (await agent.SerializeSessionAsync(session)).GetRawText());
            _sourceDocuments[conversationId] = saved;
            return session;
        }
        catch (Exception ex)
        {
            var quarantine = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(path, quarantine, overwrite: true);
            }
            catch (IOException moveError)
            {
                Console.Error.WriteLine($"[resume] could not quarantine {path}: {moveError.Message}");
            }

            Console.Error.WriteLine(
                $"[resume] session state for '{conversationId}' is unreadable " +
                $"({ex.GetType().Name}: {ex.Message}); moved it to {quarantine}, starting fresh.");
            return null;
        }
    }

    private string PathFor(string conversationId)
    {
        // The conversation id is a free-form route segment — strip anything
        // that is not a legal filename character (and with it any path
        // traversal) before it reaches the sessions directory.
        var safe = string.Join("_",
            conversationId.Split(System.IO.Path.GetInvalidFileNameChars(),
                StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(directory, $"{safe}.json");
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
