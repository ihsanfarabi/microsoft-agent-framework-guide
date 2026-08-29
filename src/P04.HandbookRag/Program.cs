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
var corpus = FindCorpusDirectory();
var chunks = corpus.GetFiles("*.md")
    .OrderBy(f => f.Name, StringComparer.Ordinal)
    .SelectMany(f => HandbookChunker.Chunk(f.Name, File.ReadAllText(f.FullName)))
    .ToList();
await retriever.BuildAsync(chunks);
Console.WriteLine($"indexed {chunks.Count} chunks from {corpus.FullName}");

// agent.RunAsync returns AgentResponse; the response object is not the text.
var agent = HandbookBot.Create(retriever);
var response = await agent.RunAsync("How many vacation days do I get?");
Console.WriteLine($"bot> {response.Text}");

// dotnet run executes from bin/Debug/net10.0, so the repo root's docs/corpus
// is several levels above the working directory. Walk up from the binary
// location until a docs/corpus directory appears — robust regardless of
// Debug/Release or publish layout.
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
