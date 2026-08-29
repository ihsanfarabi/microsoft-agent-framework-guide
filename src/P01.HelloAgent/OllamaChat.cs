using Microsoft.Extensions.AI;
using OllamaSharp;

namespace P01.HelloAgent;

/// <summary>
/// Factory for an Ollama-backed <see cref="IChatClient"/>.
/// Reused by P02+ agent projects: model defaults to the local curriculum model,
/// endpoint defaults to the local Ollama daemon and can be overridden via
/// the OLLAMA_ENDPOINT environment variable.
/// </summary>
public static class OllamaChat
{
    public static IChatClient Create(string? model = null)
    {
        var endpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434";
        return new OllamaApiClient(new Uri(endpoint), model ?? "glm-5.3-flash:cloud");
    }
}