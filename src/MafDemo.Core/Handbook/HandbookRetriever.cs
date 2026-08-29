namespace MafDemo.Core.Handbook;

public class HandbookRetriever(IEmbedder embedder)
{
    private readonly List<(float[] Vector, HandbookChunk Chunk)> _entries = [];

    public async Task BuildAsync(IReadOnlyList<HandbookChunk> chunks)
    {
        var vectors = await Task.WhenAll(chunks.Select(c => embedder.EmbedAsync(c.Text)));
        for (var i = 0; i < chunks.Count; i++)
            _entries.Add((vectors[i], chunks[i]));
    }

    public async Task<IReadOnlyList<HandbookChunk>> SearchAsync(string query, int topK = 3)
    {
        var queryVector = await embedder.EmbedAsync(query);
        return _entries
            .Select((e, i) => (Score: Cosine(queryVector, e.Vector), Index: i, e.Chunk))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(topK)
            .Select(x => x.Chunk)
            .ToList();
    }

    private static float Cosine(float[] a, float[] b)
    {
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
