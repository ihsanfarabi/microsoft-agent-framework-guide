using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI.Workflows;
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

// Ticket mutations are the workflow's observable outcomes; one shared store
// so the seeded tickets survive across the batch's three runs.
ITicketStore store = new InMemoryTicketStore();

// Seed the batch scenario (Task 3): one ticket per sensitive path — approve
// two, reject one. The Critical one escalates before pausing at the approval
// port, and is the kill-and-resume candidate (Task 4).
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

var workflow = BuildWorkflow(store, retriever);

return args.FirstOrDefault() == "resume"
    ? await RunResumeAsync(workflow, store)
    : await RunBatchAsync(workflow, store, batch);

// ---- Batch scenario: three tickets through triage -> diagnose ->
// (escalate if Critical) -> human approval, answering each RequestInfoEvent
// from the console.
static async Task<int> RunBatchAsync(Workflow workflow, ITicketStore store, List<TicketContext> batch)
{
    foreach (var ticketCtx in batch)
    {
        Console.WriteLine();
        Console.WriteLine($"=== ticket {ticketCtx.TicketId} ({ticketCtx.Priority}) ===");

        await using StreamingRun handle = await InProcessExecution.RunStreamingAsync(workflow, ticketCtx);
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
                    Console.Write("approve? (y/n + optional note): ");
                    var line = Console.ReadLine() ?? "n";
                    var ok = line.StartsWith('y');
                    var note = line.Length > 1 ? line[1..].Trim() : "";
                    await handle.SendResponseAsync(reqEvt.Request.CreateResponse(new ApprovalDecision(ok, note)));
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
    }

    // Assert outcomes: two Resolved, one InProgress — plus the notes.
    Console.WriteLine();
    Console.WriteLine("=== final store state ===");
    foreach (var ticket in await store.ListAsync())
        Console.WriteLine($"{ticket.Id} | {ticket.Priority} | {ticket.Status} | {string.Join(" / ", ticket.Notes)}");

    return 0;
}

// ---- Task 4 resume path placeholder (implemented in Task 4).
static Task<int> RunResumeAsync(Workflow workflow, ITicketStore store)
{
    Console.WriteLine("resume path: not implemented yet (Task 4)");
    return Task.FromResult(1);
}

// ---- Graph wiring. Conditional edges send the Critical ticket through the
// escalation node (decision from ApprovalPolicy, kept pure/testable); the
// approval executor sends FixApprovalRequest along the edge to the
// RequestPort, and the runtime returns the operator's ApprovalDecision to
// that same executor (HITL response routing).
static Workflow BuildWorkflow(ITicketStore store, HandbookRetriever retriever)
{
    var port = RequestPort.Create<FixApprovalRequest, ApprovalDecision>("FixApproval");
    var triageExecutor = new TriageExecutor(Agents.TriageClassifier());
    var diagnosticExecutor = new DiagnosticExecutor(new SpecialistTools(store, retriever));
    var escalationExecutor = new EscalationExecutor(Agents.EscalationEngineer());
    var approvalExecutor = new ApprovalExecutor(store);

    return new WorkflowBuilder(triageExecutor)
        .AddEdge(triageExecutor, diagnosticExecutor)
        .AddEdge(diagnosticExecutor, escalationExecutor,
            condition: (TicketContext t) => ApprovalPolicy.NeedsEscalation(t!.Priority))
        .AddEdge(diagnosticExecutor, approvalExecutor,
            condition: (TicketContext t) => !ApprovalPolicy.NeedsEscalation(t!.Priority))
        .AddEdge(escalationExecutor, approvalExecutor)
        .AddEdge(approvalExecutor, port)      // request: FixApprovalRequest -> port
        .AddEdge(port, approvalExecutor)      // response: ApprovalDecision back
        .WithOutputFrom(approvalExecutor)
        .Build();
}

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