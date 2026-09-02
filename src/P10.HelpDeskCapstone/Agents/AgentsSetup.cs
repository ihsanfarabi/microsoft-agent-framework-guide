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
        var corpus = HandbookCorpus.Locate();
        var chunks = corpus.GetFiles("*.md")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .SelectMany(f => HandbookChunker.Chunk(f.Name, File.ReadAllText(f.FullName)))
            .ToList();
        retriever.BuildAsync(chunks).GetAwaiter().GetResult();
        chunkCount = chunks.Count;
        return retriever;
    }
}