using System.ComponentModel;
using MafDemo.Core.Handbook;
using ModelContextProtocol.Server;

[McpServerToolType]
public static partial class KnowledgeTools
{
    [McpServerTool, Description("Search the MafCorp IT handbook. Returns doc | score | excerpt lines.")]
    public static string SearchKnowledge(string query, int maxResults = 3)
        => KnowledgeScorer.Search(query, maxResults, ChunkCache.Chunks);
}

/// <summary>
/// Lazily loads and chunks the handbook corpus once per process.
/// The corpus directory comes from the first CLI arg when it is an existing
/// directory, otherwise HandbookCorpus.Locate walks up from the binary
/// location to docs/corpus.
/// </summary>
public static class ChunkCache
{
    private static readonly Lazy<IReadOnlyList<(string Doc, string Text)>> ChunksLazy =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<(string Doc, string Text)> Chunks => ChunksLazy.Value;

    private static IReadOnlyList<(string Doc, string Text)> Load()
    {
        var args = Environment.GetCommandLineArgs();
        var overridePath = args.Length > 1 && Directory.Exists(args[1]) ? args[1] : null;
        var corpus = HandbookCorpus.Locate(overridePath);
        var chunks = new List<(string, string)>();
        foreach (var file in corpus.GetFiles("*.md").OrderBy(f => f.Name, StringComparer.Ordinal))
            foreach (var c in HandbookChunker.Chunk(file.Name, File.ReadAllText(file.FullName)))
                chunks.Add((c.Doc, c.Text));
        return chunks;
    }
}
