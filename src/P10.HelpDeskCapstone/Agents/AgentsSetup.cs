using MafDemo.Core.Handbook;
using P04.HandbookRag;

namespace P10.HelpDeskCapstone.Agents;

/// <summary>
/// Startup construction shared by all hosted agents: the handbook vector
/// index is built once here (embed every corpus chunk — a few seconds with
/// local bge-m3) and handed to every agent factory that needs grounding.
/// </summary>
public static class AgentsSetup
{
    public static HandbookRetriever BuildRetriever(out int chunkCount)
    {
        var retriever = new HandbookRetriever(new OllamaEmbedder());
        var corpus = FindCorpusDirectory();
        var chunks = corpus.GetFiles("*.md")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .SelectMany(f => HandbookChunker.Chunk(f.Name, File.ReadAllText(f.FullName)))
            .ToList();
        retriever.BuildAsync(chunks).GetAwaiter().GetResult();
        chunkCount = chunks.Count;
        return retriever;
    }

    // dotnet run executes from bin/<cfg>/net10.0, so docs/corpus is several
    // levels above the working directory — walk up from the binary location
    // the same way P04/P07/P09 do.
    public static DirectoryInfo FindCorpusDirectory()
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
}