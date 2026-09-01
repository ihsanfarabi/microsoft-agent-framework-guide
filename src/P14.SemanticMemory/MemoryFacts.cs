namespace P14.SemanticMemory;

/// <summary>
/// Shared constants for the P14 memory projects. Task 2/3 providers reference
/// the same values so every vector store collection in P14 is built against
/// identical embedding geometry.
/// </summary>
public static class MemoryFacts
{
    /// <summary>
    /// Output dimension of the bge-m3 embedding model served by Ollama
    /// (the configured <c>Ollama:EmbeddingModel</c>). The chat-history
    /// vector collection must be created with exactly this many dimensions
    /// or the store rejects every upsert.
    /// </summary>
    public const int VectorDimensions = 1024;
}
