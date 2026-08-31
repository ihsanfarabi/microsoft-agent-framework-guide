using System.Text.Json;
using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using P04.HandbookRag;
using P06.TriageComposition;
using P07.ResolutionWorkflow;
using P07.ResolutionWorkflow.Executors;

// Start OTel tracing first so the provider is listening before any model call
// (same Aspire-dashboard wiring as P05/P06: ./aspire-dashboard.sh, then
// browse http://localhost:18888 -> Traces).
using var telemetry = Telemetry.StartOtlp("P07.ResolutionWorkflow");

// Build the handbook vector index once at startup — the P06 specialists the
// diagnosis node wraps ground their fixes with it (REUSE, NOT PORT).
var retriever = new HandbookRetriever(new OllamaEmbedder());
var chunks = FindCorpusDirectory()
    .GetFiles("*.md")
    .OrderBy(f => f.Name, StringComparer.Ordinal)
    .SelectMany(f => HandbookChunker.Chunk(f.Name, File.ReadAllText(f.FullName)))
    .ToList();
await retriever.BuildAsync(chunks);
Console.WriteLine($"indexed {chunks.Count} handbook chunks");

// File-backed ticket mutations are the workflow's observable outcomes — and
// the ticket states must survive the Task 4 kill-and-resume restart, unlike
// P06's InMemoryTicketStore. Git-ignored next to the run directory.
ITicketStore store = new FileTicketStore("p07-tickets.json");

// ---- Checkpoint plumbing (Task 4). Checkpointing is always-on for the
// batch: a JSON checkpoint store persists each super step to disk, and every
// SuperStepCompletedEvent refreshes a one-line checkpoint-info file so the
// next process knows what to restore. Per the MAF docs, "pending requests
// are also saved as part of the checkpoint state" — so killing the process
// while an approval prompt waits (answer "k" at the prompt, or Ctrl-C) is
// recoverable. Two checkpoints per HITL cycle: one right after the
// RequestInfoEvent goes out (the one we resume from), one after the answer.
const string CheckpointDirName = "p07-checkpoints";
const string CheckpointInfoFile = "p07-checkpoint-info.json";

return args.FirstOrDefault() == "resume"
    ? await RunResumeAsync(store, retriever)
    : await RunBatchAsync(store, retriever);

static (CheckpointManager manager, DirectoryInfo dir) MakeCheckpointManager()
{
    var dir = new DirectoryInfo(CheckpointDirName);
    if (!dir.Exists) dir.Create();
    return (CheckpointManager.CreateJson(new FileSystemJsonCheckpointStore(dir)), dir);
}

static async Task SaveCheckpointInfoAsync(CheckpointInfo info)
{
    var record = new { info.SessionId, info.CheckpointId };
    await File.WriteAllTextAsync(CheckpointInfoFile, JsonSerializer.Serialize(record));
}

// ---- Batch scenario: three tickets through triage -> diagnose ->
// (escalate if Critical) -> human approval, answering each RequestInfoEvent
// from the console. Answering "k" at an approval prompt (or Ctrl-C) kills the
// process without answering; `dotnet run -- resume` restores from the
// checkpoint, and the pending request is re-emitted.
static async Task<int> RunBatchAsync(ITicketStore store, HandbookRetriever retriever)
{
    var (checkpointManager, _) = MakeCheckpointManager();
    if (File.Exists(CheckpointInfoFile)) File.Delete(CheckpointInfoFile);

    // Seed fresh per batch — one ticket per sensitive path: approve two,
    // reject one; the Critical one escalates before pausing at the port.
    var batch = new List<TicketContext>();
    foreach (var (title, description, priority) in new (string, string, TicketPriority)[]
    {
        ("Wi-Fi drops every 5 minutes", "Wireless connection to the office network reconnects repeatedly throughout the day", TicketPriority.High),
        ("Excel crashes on open", "Excel crashes immediately whenever opening any spreadsheet since this morning's update", TicketPriority.Normal),
        ("Laptop encrypted, stolen", "Employee laptop with customer data was stolen from a car and is fully disk-encrypted", TicketPriority.Critical),
    })
    {
        var ticket = await store.CreateAsync(title, description, priority);
        batch.Add(new TicketContext(ticket.Id, title, description, priority, Triage: "", Diagnosis: "", ProposedFix: null, OperatorNote: null));
    }

    return await RunTicketsAsync(store, retriever, batch, checkpointManager);
}

// ---- Resume (Task 4): restore the run that was killed mid-approval, answer
// the re-emitted pending request, then carry on with any tickets still Open
// in the store (the ones never reached before the kill) so the batch still
// completes across the restart.
static async Task<int> RunResumeAsync(ITicketStore store, HandbookRetriever retriever)
{
    var (checkpointManager, _) = MakeCheckpointManager();

    if (!File.Exists(CheckpointInfoFile))
    {
        Console.WriteLine("no checkpoint info found — nothing to resume");
        return 1;
    }

    var record = JsonSerializer.Deserialize<CheckpointRecord>(
        await File.ReadAllTextAsync(CheckpointInfoFile))
        ?? throw new InvalidOperationException("corrupt checkpoint info file");
    var checkpoint = new CheckpointInfo(record.SessionId, record.CheckpointId);

    Console.WriteLine($"restoring checkpoint {record.CheckpointId} (session {record.SessionId})");
    // A Workflow instance is owned by its runner — a Workflow cannot be
    // resumed in-process after one of its runs already ended. Rebuild it with
    // identical topology and executor ids (the rehydrate doc's requirement)
    // instead of reusing the run that died.
    Workflow workflow = BuildWorkflow(store, retriever);
    await using StreamingRun handle = await InProcessExecution.ResumeStreamingAsync(workflow, checkpoint, checkpointManager);

    var resumedTicket = await DriveEventsAsync(handle);
    if (resumedTicket is { } id)
        Console.WriteLine($"[resumed] pending approval for ticket {id} was answered; run complete");

    // Whatever the kill interrupted mid-run: the tickets still Open in the
    // store were never reached or are back in the queue — run them now.
    var remaining = (await store.ListAsync())
        .Where(t => t.Status == TicketStatus.Open)
        .Select(t => new TicketContext(t.Id, t.Title, t.Description, t.Priority, Triage: "", Diagnosis: "", ProposedFix: null, OperatorNote: null))
        .ToList();
    if (remaining.Count > 0)
        await RunTicketsAsync(store, retriever, remaining, checkpointManager);

    await PrintStoreState(store);
    return 0;
}

// ---- Shared batch/continuation driver. A Workflow instance is owned by its
// runner — one run per instance — so each ticket's run gets a freshly built
// graph with identical topology and executor ids (also what a checkpoint
// resume needs to match).
static async Task<int> RunTicketsAsync(
    ITicketStore store, HandbookRetriever retriever, List<TicketContext> batch, CheckpointManager checkpointManager)
{
    foreach (var ticketCtx in batch)
    {
        if (await TicketClosedAsync(store, ticketCtx.TicketId)) continue;

        Console.WriteLine();
        Console.WriteLine($"=== ticket {ticketCtx.TicketId} ({ticketCtx.Priority}) ===");

        await using StreamingRun handle = await InProcessExecution.RunStreamingAsync(
            BuildWorkflow(store, retriever), ticketCtx, checkpointManager);
        await DriveEventsAsync(handle, checkpointManager);
    }

    // Assert outcomes: states and notes are printed after the batch.
    await PrintStoreState(store);
    return 0;
}

static async Task<bool> TicketClosedAsync(ITicketStore store, Guid id)
{
    var t = await store.GetAsync(id);
    return t is { Status: TicketStatus.Resolved or TicketStatus.InProgress };
}

static async Task PrintStoreState(ITicketStore store)
{
    Console.WriteLine();
    Console.WriteLine("=== store state ===");
    foreach (var ticket in await store.ListAsync())
        Console.WriteLine($"{ticket.Id} | {ticket.Priority} | {ticket.Status} | {string.Join(" / ", ticket.Notes)}");
}

// ---- Event loop shared by fresh and resumed runs. Answering "k" at the
// prompt kills the process without answering — that's the Task 4 scenario
// (the checkpoint was already saved when the super step carrying the pending
// request completed). Returns the ticket id whose approval was answered, if
// any (the resume path reports it).
static async Task<Guid?> DriveEventsAsync(StreamingRun handle, CheckpointManager? checkpointManager = null)
{
    Guid? answeredTicket = null;
    await foreach (WorkflowEvent evt in handle.WatchStreamAsync())
    {
        switch (evt)
        {
            case RequestInfoEvent reqEvt:
                if (!reqEvt.Request.TryGetDataAs<FixApprovalRequest>(out var request))
                {
                    Console.Error.WriteLine($"[unexpected request] {reqEvt.Request.PortInfo.RequestType}");
                    continue;
                }
                Console.WriteLine($"PROPOSED FIX: {request.ProposedFix}");
                Console.Write("approve? (y/n/k to kill + optional note): ");
                var line = Console.ReadLine() ?? "n";
                var choice = line.Length > 0 ? line[0].ToString().ToLowerInvariant() : "n";
                if (choice == "k")
                {
                    // Kill WITHOUT answering — the super step that emitted this
                    // request already checkpointed it. Simulate SIGKILL here and
                    // now; `dotnet run -- resume` is the restart path.
                    Console.WriteLine($"[killed] stopping mid-approval; restart with `dotnet run -- resume`");
                    Environment.Exit(137);
                }
                var note = line.Length > 1 ? line[1..].Trim() : "";
                answeredTicket = request.TicketId;
                await handle.SendResponseAsync(
                    reqEvt.Request.CreateResponse(new ApprovalDecision(choice == "y", note)));
                break;

            case SuperStepCompletedEvent superStep:
                if (superStep.CompletionInfo?.Checkpoint is { } ckpt && checkpointManager is not null)
                    await SaveCheckpointInfoAsync(ckpt);
                break;

            case WorkflowOutputEvent outEvt:
                Console.WriteLine($"[done] {outEvt.Data}");
                break;

            case WorkflowErrorEvent errEvt:
                Console.Error.WriteLine($"[workflow error] {errEvt.Exception}");
                break;

            case ExecutorFailedEvent failEvt:
                Console.Error.WriteLine($"[executor failed: {failEvt.ExecutorId}] {failEvt.Data}");
                break;
        }
    }

    return answeredTicket;
}

// ---- Graph wiring lives in ResolutionWorkflowFacts.Build (verbatim move —
// P09.DurableHost builds the same graph through the public factory).
static Workflow BuildWorkflow(ITicketStore store, HandbookRetriever retriever) =>
    ResolutionWorkflowFacts.Build(store, retriever);

// dotnet run executes from bin/Debug/net10.0, so docs/corpus is several
// levels above the working directory — walk up from the binary location the
// same way P04/P06 do.
static DirectoryInfo FindCorpusDirectory()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var probe = Path.Combine(dir.FullName, "docs", "corpus");
        if (Directory.Exists(probe))
            return new DirectoryInfo(probe);
    }

    throw new DirectoryNotFoundException(
        $"could not find docs/corpus in any parent of {AppContext.BaseDirectory}");
}
// ---- Disk record for the resume path: which session/checkpoint to restore.
internal sealed record CheckpointRecord(string SessionId, string CheckpointId);
