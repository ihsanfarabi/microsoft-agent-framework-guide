using MafDemo.AgentCommon;
using MafDemo.Core.Handbook;
using OllamaSharp;

namespace P04.HandbookRag;

/// <summary>
/// <see cref="IEmbedder"/> backed by Ollama's /api/embed endpoint. The client
/// (endpoint + embedding model) comes from <see cref="OllamaEmbedding"/>, which
/// resolves config the same way as <see cref="OllamaChat"/>, so this class
/// carries no configuration plumbing of its own.
/// </summary>
public class OllamaEmbedder : IEmbedder
{
    private readonly IOllamaApiClient _client;

    /// <summary>Client from <see cref="OllamaEmbedding.Create"/> (bge-m3 by default).</summary>
    public OllamaEmbedder() : this(OllamaEmbedding.Create()) { }

    /// <summary>Injectable for tests or non-default endpoints.</summary>
    public OllamaEmbedder(IOllamaApiClient client) => _client = client;

    public async Task<float[]> EmbedAsync(string text)
    {
        // OllamaApiClientExtensions.EmbedAsync posts to /api/embed for the
        // client's selected model; the response carries one embedding per
        // input, and we always send exactly one input.
        var response = await _client.EmbedAsync(text);
        return response.Embeddings is { Count: > 0 } embeddings
            ? embeddings[0]
            : throw new InvalidOperationException($"Ollama returned no embedding for input of {text.Length} chars");
    }
}
