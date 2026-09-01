using Microsoft.Extensions.VectorData;

namespace MafDemo.Core.Memory;

/// <summary>
/// A single durable fact remembered for a user, stored in a vector store
/// collection with its embedding attached.
/// </summary>
/// <remarks>
/// Carries the MEVD record attributes ([VectorStoreKey]/[VectorStoreData]/
/// [VectorStoreVector]) so it can live directly in a vector store collection.
/// Note there is no [VectorStoreRecord] attribute in vectordata abstractions
/// 10.7.0 — properties are attributed individually.
/// </remarks>
public record MemoryFact(
    [property: VectorStoreKey] string Id,
    [property: VectorStoreData] string UserId,
    [property: VectorStoreData] string Text,
    [property: VectorStoreData] DateTimeOffset CreatedAt,
    [property: VectorStoreVector(MemoryFact.EmbeddingDimensions)] float[] Vector)
{
    /// <summary>
    /// Declared dimension of the embedding vector. Matches bge-m3 (the
    /// production Ollama embedding model, 1024 dims); the InMemory store
    /// does not validate stored vector length against it, so any embedder
    /// (including small offline fakes) works.
    /// </summary>
    public const int EmbeddingDimensions = 1024;

    // The MEVD model builder requires a parameterless constructor; a
    // positional record alone does not provide one.
    public MemoryFact() : this("", "", "", default, []) { }
}
