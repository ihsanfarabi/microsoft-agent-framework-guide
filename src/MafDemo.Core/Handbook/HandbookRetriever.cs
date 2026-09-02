namespace MafDemo.Core.Handbook;

/// <summary>
/// Keyword/cosine handbook index. <see cref="SearchAsync"/> applies a minimum
/// similarity floor (<see cref="DefaultMinScore"/>) — below it the caller's
/// "no handbook match" branch runs; without a floor, zero-score chunks were
/// returned as if they matched.
/// </summary>
public class HandbookRetriever(IEmbedder embedder)
{
    /// <summary>Minimum cosine similarity for a chunk to count as a match.</summary>
    public const float DefaultMinScore = 0.3f;

    private readonly List<(float[] Vector, HandbookChunk Chunk)> _entries = [];

    /// <summary>(Re)builds the index from <paramref name="chunks"/> — clears
    /// first, so a rebuild (fresh corpus, same process) never duplicates
    /// entries.</summary>
    public async Task BuildAsync(IReadOnlyList<HandbookChunk> chunks)
    {
        _entries.Clear();
        var vectors = await Task.WhenAll(chunks.Select(c => embedder.EmbedAsync(c.Text)));
        for (var i = 0; i < chunks.Count; i++)
            _entries.Add((vectors[i], chunks[i]));
    }

    /// <summary>Returns the up to <paramref name="topK"/> chunks scoring at
    /// least <paramref name="minScore"/> against the query, best first.
    /// Pass <c>minScore: 0f</c> for the old always-topK behavior.</summary>
    public async Task<IReadOnlyList<HandbookChunk>> SearchAsync(
        string query, int topK = 3, float minScore = DefaultMinScore)
    {
        var queryVector = await embedder.EmbedAsync(query);
        return _entries
            .Select((e, i) => (Score: Cosine(queryVector, e.Vector), Index: i, e.Chunk))
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(topK)
            .Select(x => x.Chunk)
            .ToList();
    }

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            // The one failure mode a dimension check exists for: the embedder
            // changed (e.g. OLLAMA_EMBEDDING_MODEL switched to a different
            // model) between build and query. A bare IndexOutOfRangeException
            // from the loop below would say nothing about the actual cause.
            throw new InvalidOperationException(
                $"embedding dimension mismatch: query has {a.Length} dims, chunk has {b.Length} — " +
                "was the embedding model changed after BuildAsync? Rebuild the retriever.");
        }

        var dot = 0f;
        var normA = 0f;
        var normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0f || normB == 0f) return 0f;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}