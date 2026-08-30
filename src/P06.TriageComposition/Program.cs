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

var tools = new SpecialistTools(store, retriever);

// Smoke scenarios: one prompt per specialist. Network/Hardware questions are
// handbook facts; the software question must round-trip through get_ticket.
var scenarios = new (string Label, ChatClientAgent Agent, string Prompt)[]
{
    ("network", Specialists.NetworkSpecialist(tools),
        "My VPN connection drops every few hours and asks for login again. What should I do?"),
    ("software", Specialists.SoftwareSpecialist(tools),
        $"What is the status of ticket {seeded.Id}?"),
    ("hardware", Specialists.HardwareSpecialist(tools),
        "The office printer shows an offline status and nothing prints. What does the handbook say I should check?"),
};

foreach (var (label, agent, prompt) in scenarios)
{
    Console.WriteLine();
    Console.WriteLine($"=== {label} specialist ===");
    Console.WriteLine($"user> {prompt}");
    var response = await agent.RunAsync(prompt);
    Console.WriteLine($"{label}-bot> {response.Text}");
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
