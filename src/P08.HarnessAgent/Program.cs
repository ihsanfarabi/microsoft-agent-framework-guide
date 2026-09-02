using System.Runtime.InteropServices;
using System.Text.Json;
using MafDemo.AgentCommon;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using P08.HarnessAgent;

// One work/ area next to the binary: the harness agent's file-access store
// (Handbook copies land in work/handbook/ here), the file-backed ticket store
// the ticket tools mutate, and — Task 4 — work/session-state/, where the
// session is checkpointed so a killed batch resumes instead of starting over.
// Runtime state, git-ignored.
var workRoot = Path.Combine(AppContext.BaseDirectory, "work");
Directory.CreateDirectory(workRoot);
CopyHandbook(workRoot);

var store = new FileTicketStore(Path.Combine(workRoot, "tickets.json"));
await BacklogSeed.RunAsync(store);

// Console logging on the harness internals: the batch is a long autonomous
// run, and the MAF harness logs one line per model turn through the logger
// factory accepted by AsHarnessAgent — without it the run is silent for
// minutes (see the task report for how this logging also drove diagnosis).
using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o => o.SingleLine = true));

// Ruling-3 resolution: NO UseFunctionInvocation wrapper — the harness wires
// its own function-invocation layer around the client (verified in the MAF
// HarnessAgent source and at runtime; see the task report).
var agent = HarnessFacts.Build(OllamaChat.Create(), TicketTools.All(new TicketTools(store)), loggerFactory);

// Kill-and-resume (Task 4): the session — chat history, todo list, standing
// approvals, and the session id that file memory is keyed by — is checkpointed
// to work/session-state/session.json and rehydrated here on the next start.
// The brief said "serialize on exit", but a mid-batch kill is a SIGINT/SIGTERM,
// which by default terminates with no exit path; checkpoints are therefore
// taken continuously (per tool call and per completed run) and the signals are
// converted to a cooperative wind-down (below), so the kill cannot skip past
// the last persisted progress.
var sessionStateDir = Path.Combine(workRoot, "session-state");
var sessionStatePath = Path.Combine(sessionStateDir, "session.json");
Directory.CreateDirectory(sessionStateDir);

AgentSession session;
if (File.Exists(sessionStatePath))
{
    try
    {
        // The JsonDocument is deliberately kept alive for the process
        // lifetime: deserialized session-state values may hold JsonElements
        // backed by it, and this CLI holds the restored session until it
        // exits anyway.
        var saved = JsonDocument.Parse(await File.ReadAllTextAsync(sessionStatePath));
        session = await agent.DeserializeSessionAsync(saved.RootElement);
        Console.WriteLine($"[resume] session restored from {sessionStatePath}");
    }
    catch (Exception ex)
    {
        // Corrupt/truncated state must brick startup loudly, not silently.
        // The unreadable file is moved aside (preserved for inspection and
        // never overwritten by the next save), the operator is told exactly
        // what happened and what to do, and the process exits. It does NOT
        // fall back to a fresh session: a fresh agent would lose the todo
        // list and history and could redo tickets the killed run finished.
        var quarantine = $"{sessionStatePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(sessionStatePath, quarantine);
        Console.Error.WriteLine($"[fatal] saved session state is unreadable: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine($"[fatal] moved it to {quarantine} (preserved, not overwritten).");
        Console.Error.WriteLine($"[fatal] refusing to start a fresh session automatically — that could redo finished tickets.");
        Console.Error.WriteLine("[fatal] inspect the quarantined file, then start again to begin a fresh batch.");
        return 1;
    }
}
else
{
    session = await agent.CreateSessionAsync();
    Console.WriteLine($"[session] fresh session; checkpoints -> {sessionStatePath}");
}

// Cooperative shutdown: a kill signal cancels the token instead of letting
// the runtime's default disposition terminate the process; the streaming
// loop unwinds through the token and the interrupt-path checkpoint persists
// the session before exit. SIGTERM — the signal an operator sends to a
// detached/overnight run — rides PosixSignalRegistration. SIGINT arrives as
// Console.CancelKeyPress when run at a console (Ctrl+C); note that a process
// backgrounded from a non-interactive shell inherits SIGINT set to ignore
// (POSIX), and .NET honors that inherited disposition, so `kill -INT` on a
// detached run is silently a no-op for both handlers — kill -TERM is the
// documented way to stop an unattended batch (verified with a minimal repro;
// see the task report).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[interrupt] SIGINT received — winding down");
    cts.Cancel();
};
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    // Cancel=true suppresses the default SIGTERM disposition — without it the
    // runtime terminates the process as soon as this callback returns, and
    // the wind-down below never runs (verified with a minimal repro; see the
    // task report).
    context.Cancel = true;
    Console.WriteLine("\n[interrupt] SIGTERM received — winding down");
    cts.Cancel();
});

// Canonical harness drive loop (MAF get-started doc): the multi-step run
// progresses as streaming updates are enumerated. close_ticket is an
// ApprovalRequiredAIFunction, so when the model asks to close, the harness
// ends the run surfacing ToolApprovalRequestContent(s) — answer each on the
// console (y/n/a) and resume the same session. A single turn can surface
// SEVERAL pending calls at once (the model sometimes bursts all its closes
// in one go): every request is collected and answered, one approval-response
// content per call in a single user message (the harness's
// ApprovalResponseBindingChatClient matches responses to calls by call id).
// "a" records a standing rule, after which later closes auto-pass with no
// prompt.
IReadOnlyList<ToolApprovalRequestContent> approvalRequests;
try
{
    approvalRequests = await DriveAsync(new ChatMessage(ChatRole.User, "Work the ticket backlog."));
    while (approvalRequests.Count > 0 && !cts.IsCancellationRequested)
    {
        var responses = approvalRequests.Select(PromptApproval).ToList();
        approvalRequests = await DriveAsync(new ChatMessage(ChatRole.User, responses));
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.WriteLine("\n[interrupt] run cancelled mid-batch");
    await CheckpointAsync("interrupt", CancellationToken.None);
}
Console.WriteLine();

// Post-run evidence: what the batch actually did to the store.
Console.WriteLine("\n---- final ticket state ----");
foreach (var t in await store.ListAsync())
    Console.WriteLine($"{t.Id} | {t.Status} | {t.Priority} | {t.Title}"
        + (t.Notes.Count == 0 ? "" : $" | {t.Notes.Count} note(s)"));

return 0;

/// <summary>Drives one harness run over the session, streaming updates to the
/// console. Returns every approval request the run surfaced — a single turn
/// can queue several gated calls, and each one must be answered or the run
/// tangles (the final review's multi-request fix). The cancellation token
/// unwinds the enumeration on SIGINT/SIGTERM; a checkpoint is taken per issued
/// tool call (see <see cref="CheckpointAsync"/>) and once more when the run
/// completes.</summary>
async Task<IReadOnlyList<ToolApprovalRequestContent>> DriveAsync(ChatMessage prompt)
{
    var requests = new List<ToolApprovalRequestContent>();
    await foreach (var update in agent.RunStreamingAsync(prompt, session, cancellationToken: cts.Token))
    {
        Console.Write(update);
        // Collect every pending request, deduped by call id: answering the
        // same call twice would cross the wires the same way dropping one
        // did.
        foreach (var request in update.Contents.OfType<ToolApprovalRequestContent>())
            if (!requests.Exists(seen => seen.ToolCall.CallId == request.ToolCall.CallId))
                requests.Add(request);
        foreach (var call in update.Contents.OfType<FunctionCallContent>())
        {
            // Mid-run snapshot: the harness persists chat history to the
            // session per model call, so a checkpoint taken as a tool call
            // streams out lands on a completed model turn — though note the
            // call may not have EXECUTED yet when the checkpoint fires (the
            // resume hazard documented in NOTES.md; acceptable because the
            // ticket store is the truth and a re-issued close is re-gated).
            await CheckpointAsync($"mid-run: {call.Name}", cts.Token);
        }
    }

    await CheckpointAsync("run complete", cts.Token);
    return requests;
}

/// <summary>Serializes the session to <c>work/session-state/session.json</c>
/// (atomic temp-file move, so a kill during the write cannot leave a torn
/// file for the next start to rehydrate). Failures other than cancellation are
/// logged and swallowed — a checkpoint problem must never crash the batch
/// after its work is done, at any of the call sites (mid-run, run-complete,
/// interrupt): the ticket store is on disk regardless, the next successful
/// checkpoint supersedes the stale file, and the final ticket-state report
/// always prints. Cancellation is re-thrown so an interrupt still unwinds the
/// run instead of being swallowed here.</summary>
async Task CheckpointAsync(string reason, CancellationToken ct)
{
    try
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        var tmp = sessionStatePath + ".tmp";
        await File.WriteAllTextAsync(tmp, serialized.GetRawText(), ct);
        File.Move(tmp, sessionStatePath, overwrite: true);
        Console.WriteLine($"\n[checkpoint] session state saved ({reason})");
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[checkpoint] FAILED to save session state ({reason}): {ex.Message}");
    }
}

/// <summary>Prints the approval request (tool name + arguments) and reads the
/// operator's answer: <c>y</c> approves this one call, <c>a</c> approves it and
/// records a standing "always approve this tool" rule in the session state
/// (the harness's own mechanism, so later closes auto-pass), anything else
/// declines and the model is told to leave the ticket open.</summary>
static AIContent PromptApproval(ToolApprovalRequestContent request)
{
    // The request's ToolCall is the model's original call — at runtime a
    // FunctionCallContent carrying the wire name and arguments (10.9's
    // ToolCallContent base type only exposes the CallId).
    var call = request.ToolCall as FunctionCallContent;
    Console.WriteLine($"\n[approval] {call?.Name ?? "unknown tool"} {JsonSerializer.Serialize(call?.Arguments)}");
    Console.Write("[approval] allow? (y = once, a = always for this tool, n = decline): ");
    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
    return answer switch
    {
        "a" => request.CreateAlwaysApproveToolResponse("operator: always approve this tool"),
        "y" => request.CreateResponse(approved: true, reason: "operator approved"),
        _ => request.CreateResponse(approved: false, reason: "operator declined"),
    };
}

/// <summary>Copies the shared handbook corpus (docs/corpus) into the file-access
/// store's handbook/ folder, so the agent's file_access_ls/grep/read tools
/// have real policy docs to consult during the batch.</summary>
static void CopyHandbook(string workRoot)
{
    var corpus = HandbookCorpus.Locate();
    var target = Path.Combine(workRoot, "handbook");
    Directory.CreateDirectory(target);
    foreach (var file in corpus.GetFiles("*.md"))
        File.Copy(file.FullName, Path.Combine(target, file.Name), overwrite: true);
    Console.WriteLine($"copied {corpus.GetFiles("*.md").Length} handbook docs to {target}");
}
