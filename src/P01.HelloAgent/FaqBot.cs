using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P01.HelloAgent;

/// <summary>
/// Minimal FAQ agent: a ChatClientAgent wired to the Ollama chat client.
/// No tools, no session — one-shot question answering only.
/// </summary>
public static class FaqBot
{
    public static ChatClientAgent Create(string instructions) =>
        new(OllamaChat.Create(), name: "FaqBot", instructions: instructions);
}