using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace MafDemo.Core.Memory;

/// <summary>
/// Dedupe-aware fact memory: stores per-user facts with embeddings, upserts
/// near-duplicates (cosine ≥ <see cref="DuplicateThreshold"/>) in place
/// instead of adding a second record, and persists the collection to a JSON
/// file so facts survive process restarts.
/// </summary>
/// <remarks>
/// Backed by the MEVD InMemory vector store (CommunityToolkit.VectorData.InMemory).
/// Text is embedded with the injected <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
/// (OllamaApiClient implements it natively); search passes the query vector
/// directly, so the store works with any embedder — including small offline
/// fakes in tests.
/// </remarks>
public sealed class FactMemoryStore
{
    /// <summary>
    /// Cosine similarity at or above which an incoming fact is treated as a
    /// duplicate of an existing one and upserted (updated) instead of added.
    /// </summary>
    public const double DuplicateThreshold = 0.9;

    private const string CollectionName = "memory-facts";

    // Upper bound on how many of a user's facts are considered for dedupe on
    // a single add. Far above realistic per-user fact counts.
    private const int MaxFactsPerUser = 10_000;

    private readonly InMemoryVectorStore _vectorStore = new();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly VectorStoreCollection<string, MemoryFact> _collection;

    public FactMemoryStore(IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _embedder = embedder;
        _collection = _vectorStore.GetCollection<string, MemoryFact>(CollectionName);
    }

    /// <summary>
    /// Adds a fact for the user, or — when an existing fact of the same user
    /// is a cosine ≥ <see cref="DuplicateThreshold"/> neighbor of the new
    /// text — updates that fact in place (same Id, new text/vector).
    /// </summary>
    public async Task<MemoryFact> AddAsync(string userId, string text)
    {
        await _collection.EnsureCollectionExistsAsync();
        var vector = (await EmbedAsync(text)).Vector;

        MemoryFact? duplicate = null;
        double bestSimilarity = 0;
        await foreach (var fact in _collection.GetAsync(
                           f => f.UserId == userId,
                           top: MaxFactsPerUser,
                           new FilteredRecordRetrievalOptions<MemoryFact> { IncludeVectors = true }))
        {
            var similarity = CosineSimilarity(vector, fact.Vector);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                duplicate = fact;
            }
        }

        if (duplicate is not null && bestSimilarity >= DuplicateThreshold)
        {
            var updated = duplicate with { Text = text, CreatedAt = DateTimeOffset.UtcNow, Vector = vector.ToArray() };
            await _collection.UpsertAsync(updated);
            return updated;
        }

        var added = new MemoryFact(Guid.NewGuid().ToString(), userId, text, DateTimeOffset.UtcNow, vector.ToArray());
        await _collection.UpsertAsync(added);
        return added;
    }

    /// <summary>
    /// Recalls up to <paramref name="topK"/> facts of <paramref name="userId"/>
    /// most similar to <paramref name="query"/>, best match first.
    /// </summary>
    public async Task<IReadOnlyList<MemoryFact>> RecallAsync(string userId, string query, int topK = 3)
    {
        await _collection.EnsureCollectionExistsAsync();
        var vector = (await EmbedAsync(query)).Vector;

        var results = new List<MemoryFact>();
        await foreach (var result in _collection.SearchAsync(
                           vector,
                           topK,
                           new VectorSearchOptions<MemoryFact> { Filter = f => f.UserId == userId }))
        {
            results.Add(result.Record);
        }

        return results;
    }

    /// <summary>
    /// Lists every fact of <paramref name="userId"/>, oldest first. Pure
    /// collection enumeration — no model call, no embedding.
    /// </summary>
    public async Task<IReadOnlyList<MemoryFact>> ListAsync(string userId)
    {
        await _collection.EnsureCollectionExistsAsync();
        var facts = new List<MemoryFact>();
        await foreach (var fact in _collection.GetAsync(
                           f => f.UserId == userId,
                           top: MaxFactsPerUser,
                           new FilteredRecordRetrievalOptions<MemoryFact> { IncludeVectors = false }))
        {
            facts.Add(fact);
        }

        return facts.OrderBy(f => f.CreatedAt).ToList();
    }

    /// <summary>
    /// Deletes every fact of <paramref name="userId"/>. Returns how many were
    /// removed. Persist with <see cref="SaveAsync"/> to make the clear survive
    /// a process restart.
    /// </summary>
    public async Task<int> ClearAsync(string userId)
    {
        await _collection.EnsureCollectionExistsAsync();
        var keys = new List<string>();
        await foreach (var fact in _collection.GetAsync(f => f.UserId == userId, top: MaxFactsPerUser))
        {
            keys.Add(fact.Id);
        }

        foreach (var key in keys)
        {
            await _collection.DeleteAsync(key);
        }

        return keys.Count;
    }

    /// <summary>Serializes the fact collection to <paramref name="path"/> as JSON.</summary>
    public async Task SaveAsync(string path)
    {
        await _collection.EnsureCollectionExistsAsync();
        using var stream = File.Create(path);
        await _vectorStore.SerializeCollectionAsJsonAsync<string, MemoryFact>(CollectionName, stream);
    }

    /// <summary>
    /// Loads a previously saved collection from <paramref name="path"/>. A
    /// missing file is treated as an empty store (no-op).
    /// </summary>
    public async Task LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = File.OpenRead(path);
        await _vectorStore.DeserializeCollectionFromJsonAsync<string, MemoryFact>(stream);
    }

    private async Task<Embedding<float>> EmbedAsync(string text) =>
        (await _embedder.GenerateAsync([text])).Single();

    private static double CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var x = a.Span;
        var y = b.Span;
        if (x.Length == 0 || x.Length != y.Length)
        {
            return 0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < x.Length; i++)
        {
            dot += x[i] * y[i];
            normA += x[i] * x[i];
            normB += y[i] * y[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
