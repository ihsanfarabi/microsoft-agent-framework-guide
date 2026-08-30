using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
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

// Task 2: agents-as-tools triage. One front-desk TriageAgent; the specialists
// are its named function tools (network_connectivity / software_support /
// hardware_support) and pick their own tools per Task 1. Smoke scenarios ask
// exactly one question per specialist — expect every answer prefixed by which
// specialist handled it.
var tools = new SpecialistTools(store, retriever);
var triage = TriageAsTools.Create(tools);

var scenarios = new (string Label, string Prompt)[]
{
    ("network", "My Wi-Fi drops every 5 minutes"),
    ("software", "Excel crashes on open"),
    ("hardware", "Laptop won't charge"),
};

foreach (var (label, prompt) in scenarios)
{
    Console.WriteLine();
    Console.WriteLine($"=== {label} ===");
    Console.WriteLine($"user> {prompt}");
    var response = await triage.RunAsync(prompt);
    Console.WriteLine("triage>");
    Console.WriteLine(response.Text);
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
