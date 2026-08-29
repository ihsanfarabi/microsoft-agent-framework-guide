using MafDemo.Core.Handbook;

namespace P04.HandbookRag;

/// <summary>
/// Tool surface for the tool-based retrieval variant (Task 5): the model is
/// handed <c>search_handbook</c> and must choose to call it, instead of having
/// chunks auto-injected on every turn by <see cref="HandbookContextProvider"/>.
/// <see cref="SearchCount"/> exists for the P04 comparison run — Program.cs
/// reads it after each scenario to report how many times the model actually
/// retrieved, which is the empirical point of the variant.
/// </summary>
public class HandbookToolFunctions(HandbookRetriever retriever)
{
    private const int TopK = 3;

    /// <summary>Number of search_handbook calls since the last <see cref="ResetSearchCount"/>.</summary>
    public int SearchCount { get; private set; }

    /// <summary>Zeroes the counter; called between scenarios by Program.cs.</summary>
    public void ResetSearchCount() => SearchCount = 0;

    /// <summary>
    /// Tool body: embed the query, cosine-rank against the same in-memory index
    /// the context provider uses, and return the top chunks as "[doc #index]"
    /// blocks joined by "---" — the exact shape the provider injects, so citation
    /// behavior is comparable across the two variants.
    /// </summary>
    public async Task<string> SearchHandbookAsync(string query)
    {
        SearchCount++;
        var hits = await retriever.SearchAsync(query, topK: TopK);
        return hits.Count == 0
            ? "(no handbook excerpts matched)"
            : string.Join("\n---\n", hits.Select(h => $"[{h.Doc} #{h.Index}]\n{h.Text}"));
    }
}
