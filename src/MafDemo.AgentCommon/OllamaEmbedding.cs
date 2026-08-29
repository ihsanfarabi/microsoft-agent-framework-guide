using OllamaSharp;

namespace MafDemo.AgentCommon;

/// <summary>
/// Factory for an Ollama-backed embedding client (<see cref="OllamaApiClient"/>
/// selected onto an embedding model). Mirrors <see cref="OllamaChat"/>: endpoint
/// and model come from <c>appsettings.json</c> (<c>Ollama:Endpoint</c> /
/// <c>Ollama:EmbeddingModel</c>), overridden by the OLLAMA_ENDPOINT /
/// OLLAMA_EMBEDDING_MODEL environment variables, with local defaults last.
/// The embedding model is a separate key from <c>Ollama:Model</c> because chat
/// and embedding models differ (chat: glm-5.3-flash:cloud, embed: bge-m3).
/// </summary>
public static class OllamaEmbedding
{
    public const string DefaultModel = "bge-m3";

    /// <summary>
    /// A client whose selected model is the configured embedding model.
    /// The raw <see cref="OllamaApiClient"/> (no OTel wrapper) is returned:
    /// MAF instrumentation targets chat clients, and the caller decides how
    /// the embedding call surface looks (IEmbedder in P04).
    /// </summary>
    public static OllamaApiClient Create(string? model = null)
    {
        var endpoint = OllamaChat.Resolve("OLLAMA_ENDPOINT", "Ollama:Endpoint", "http://localhost:11434");
        model ??= OllamaChat.Resolve("OLLAMA_EMBEDDING_MODEL", "Ollama:EmbeddingModel", DefaultModel);
        return new OllamaApiClient(new Uri(endpoint), model);
    }
}
