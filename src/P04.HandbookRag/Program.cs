using System.Diagnostics;
using MafDemo.AgentCommon;
using MafDemo.Core.Handbook;
using P04.HandbookRag;

// Start OTel tracing first so the provider is listening before any model call.
using var telemetry = Telemetry.Start("P04.HandbookRag");

// Build the vector index at startup: embed every chunk of every handbook doc
// once, then each agent turn only embeds the query. At 10 docs (~60 chunks)
// this takes a few seconds with local bge-m3, so no on-disk index cache is
// kept — a stale cache would be worse than the rebuild cost.
var retriever = new HandbookRetriever(new OllamaEmbedder());
var corpus = HandbookCorpus.Locate();
var chunks = corpus.GetFiles("*.md")
    .OrderBy(f => f.Name, StringComparer.Ordinal)
    .SelectMany(f => HandbookChunker.Chunk(f.Name, File.ReadAllText(f.FullName)))
    .ToList();
await retriever.BuildAsync(chunks);
Console.WriteLine($"indexed {chunks.Count} chunks from {corpus.FullName}");

// agent.RunAsync returns AgentResponse; the response object is not the text.
// Both variants share the same retriever/index; only the grounding mechanism
// differs — provider agent: chunks auto-injected every turn; tool agent: the
// model must choose to call search_handbook.
var providerAgent = HandbookBot.Create(retriever);
var toolFunctions = new HandbookToolFunctions(retriever);
var toolAgent = HandbookBot.CreateToolVariant(toolFunctions);

// Task 4 guardrail scenarios: two grounded questions (different docs) plus
// one question the handbook cannot answer — the third must trip the
// "That is not in the handbook." fallback rather than a hallucination.
// Task 5 runs each through BOTH variants; SearchCount and the stopwatch give
// the raw comparison data recorded in docs/projects/04-handbook-rag/NOTES.md.
var scenarios = new[]
{
    "How many vacation days do I get?",
    "When must an RMA be filed?",
    "What is the CEO's home address?",
};
foreach (var question in scenarios)
{
    Console.WriteLine($"user> {question}");

    var providerWatch = Stopwatch.StartNew();
    var providerResponse = await providerAgent.RunAsync(question);
    providerWatch.Stop();
    Console.WriteLine($"provider-bot> {providerResponse.Text}");
    Console.WriteLine($"  (provider agent: {providerWatch.ElapsedMilliseconds} ms, top-3 auto-injected)");

    toolFunctions.ResetSearchCount();
    var toolWatch = Stopwatch.StartNew();
    var toolResponse = await toolAgent.RunAsync(question);
    toolWatch.Stop();
    Console.WriteLine($"tool-bot> {toolResponse.Text}");
    Console.WriteLine($"  (tool agent: {toolWatch.ElapsedMilliseconds} ms, search_handbook calls: {toolFunctions.SearchCount})");
    Console.WriteLine();
}
