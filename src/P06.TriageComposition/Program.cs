using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using P04.HandbookRag;
using P06.TriageComposition;

// Start OTel tracing first so the provider is listening before any model call.
// StartOtlp exports to the Aspire dashboard (./aspire-dashboard.sh, then
// browse http://localhost:18888 -> Traces) — the same wiring P05 uses.
using var telemetry = Telemetry.StartOtlp("P06.TriageComposition");

// Build the handbook vector index once at startup (same approach as P04:
// embed every chunk of every corpus doc, then only the query is embedded
// per turn). The OllamaEmbedder is P04's — REUSE, NOT PORT.
var retriever = new HandbookRetriever(new OllamaEmbedder());
var chunks = FindCorpusDirectory()
    .GetFiles("*.md")
    .OrderBy(f => f.Name, StringComparer.Ordinal)
    .SelectMany(f => HandbookChunker.Chunk(f.Name, File.ReadAllText(f.FullName)))
    .ToList();
await retriever.BuildAsync(chunks);
Console.WriteLine($"indexed {chunks.Count} handbook chunks");

// Seed a ticket so the software specialist's get_ticket scenario targets a
// known id instead of relying on the model to invent one.
var store = new InMemoryTicketStore();
var seeded = await store.CreateAsync(
    "Email stuck on outbox",
    "Exchange reports authentication failure since this morning",
    TicketPriority.High);
Console.WriteLine($"seeded ticket {seeded.Id}");

// Task 3: triage via handoff orchestration (default mode) — or `as-tools` to
// rerun the Task 2 composition for the trace comparison. Both scenarios use
// the same three questions; handoff runs each as one interactive conversation.
var mode = args.FirstOrDefault(a => a is "as-tools" or "handoff") ?? "handoff";
Console.WriteLine($"mode: {mode}");
if (args.Length > 0 && !args.Contains(mode))
    Console.WriteLine($"(ignored unknown arg(s) — pass \"handoff\" or \"as-tools\")");
var tools = new SpecialistTools(store, retriever);

var scenarios = new (string Label, string Prompt)[]
{
    ("network", "My Wi-Fi drops every 5 minutes"),
    ("software", "Excel crashes on open"),
    ("hardware", "Laptop won't charge"),
};

if (mode == "as-tools")
{
    // Task 2: agents-as-tools triage. One front-desk TriageAgent; the
    // specialists are its named function tools (network_connectivity /
    // software_support / hardware_support) and pick their own tools per Task 1.
    // Smoke scenarios ask exactly one question per specialist — expect every
    // answer prefixed by which specialist handled it.
    var triage = TriageAsTools.Create(tools);

    foreach (var (label, prompt) in scenarios)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {label} ===");
        Console.WriteLine($"user> {prompt}");
        var response = await triage.RunAsync(prompt);
        Console.WriteLine("triage>");
        Console.WriteLine(response.Text);
    }
}
else
{
    // Task 3: handoff workflow. Interactive per the MAF handoff doc: a run
    // ends when the holding agent answers WITHOUT a handoff tool call, control
    // returns to the caller, and the next user turn is fed by appending to the
    // conversation and running again. Each scripted scenario is one
    // conversation: one user question, then the workflow loop runs until an
    // agent finishes its turn — whoever answered last "holds" the conversation.
    var workflow = TriageHandoff.Create(tools);

    foreach (var (label, prompt) in scenarios)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {label} (handoff) ===");

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        string? lastExecutorId = null;
        List<ChatMessage> newMessages = [];
        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            if (evt is AgentResponseUpdateEvent update)
            {
                if (update.ExecutorId != lastExecutorId)
                {
                    lastExecutorId = update.ExecutorId;
                    Console.WriteLine();
                    Console.WriteLine($"{update.ExecutorId}>");
                }

                Console.Write(update.Update.Text);
            }
            else if (evt is WorkflowOutputEvent output)
            {
                newMessages = output.As<List<ChatMessage>>()!;
                break;
            }
        }

        // Control is back with the caller: merge the agents' messages into the
        // conversation, note who answered last, and take the next user turn.
        // The scripted scenarios provide only the opening question, so the
        // conversation closes here with that agent holding it; appending
        // further user turns here is what would make it multi-turn.
        messages.AddRange(newMessages.Skip(messages.Count));
        var holdingAgent = newMessages.LastOrDefault(m => m.Role == ChatRole.Assistant) is { } last
            ? (last.AuthorName ?? lastExecutorId ?? "?")
            : lastExecutorId;

        Console.WriteLine();
        Console.WriteLine($"held by: {holdingAgent}");
    }
}

// dotnet run executes from bin/Debug/net10.0, so docs/corpus is several
// levels above the working directory. Walk up from the binary location the
// same way P04 does.
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
