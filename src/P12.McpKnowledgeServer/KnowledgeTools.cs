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
/// The corpus directory comes from args[0] when supplied, otherwise it is
/// located by walking up from the binary location to docs/corpus — dotnet run
/// executes from bin/&lt;cfg&gt;/net10.0, the same walk-up P04/P07/P09/P10 use.
/// </summary>
public static class ChunkCache
{
    private static readonly Lazy<IReadOnlyList<(string Doc, string Text)>> ChunksLazy =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<(string Doc, string Text)> Chunks => ChunksLazy.Value;

    private static IReadOnlyList<(string Doc, string Text)> Load()
    {
        var corpus = CorpusDirectory();
        var chunks = new List<(string, string)>();
        foreach (var file in corpus.GetFiles("*.md").OrderBy(f => f.Name, StringComparer.Ordinal))
            foreach (var c in HandbookChunker.Chunk(file.Name, File.ReadAllText(file.FullName)))
                chunks.Add((c.Doc, c.Text));
        return chunks;
    }

    private static DirectoryInfo CorpusDirectory()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Directory.Exists(args[1]))
            return new DirectoryInfo(args[1]);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "docs", "corpus");
            if (Directory.Exists(probe))
                return new DirectoryInfo(probe);
        }

        throw new DirectoryNotFoundException(
            $"could not find docs/corpus in any parent of {AppContext.BaseDirectory}");
    }
}
