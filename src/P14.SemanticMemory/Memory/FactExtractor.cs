using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P14.SemanticMemory.Memory;

/// <summary>
/// Abstraction over the fact-extraction step so tests can inject a canned
/// extractor instead of a model (the provider must be unit-testable offline).
/// </summary>
public interface IFactExtractor
{
    /// <summary>
    /// Extracts durable third-person facts about the user from one conversation
    /// turn. Implementations should return an empty list — not throw — when
    /// there is nothing to remember; the provider additionally swallows all
    /// extractor failures as a safety net.
    /// </summary>
    Task<IReadOnlyList<string>> ExtractFactsAsync(IReadOnlyList<ChatMessage> turn, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extractor backed by a tiny <see cref="ChatClientAgent"/> on the same Ollama
/// wiring as the main agent (P14 baseline). The model is asked to answer with
/// ONLY a JSON array of short third-person sentences
/// ("User prefers email over phone"); anything unparseable is treated as "no
/// facts" rather than an error, so a chatty or off-format model can never
/// break the surrounding agent run.
/// </summary>
public sealed class ChatClientFactExtractor : IFactExtractor
{
    private const string Instructions = """
        You extract durable facts about the user from a conversation turn.
        Reply with ONLY a JSON array of short third-person sentences, e.g.
        ["User prefers email over phone", "User works night shifts"].
        Include only stable preferences, facts about the user, or commitments —
        not small talk, questions, or transient remarks. If there is nothing
        worth remembering, reply with [].
        """;

    private readonly ChatClientAgent _agent;

    public ChatClientFactExtractor(IChatClient chatClient)
    {
        _agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "FactExtractor",
            ChatOptions = new ChatOptions { Instructions = Instructions },
        });
    }

    public async Task<IReadOnlyList<string>> ExtractFactsAsync(IReadOnlyList<ChatMessage> turn, CancellationToken cancellationToken = default)
    {
        var transcript = string.Join("\n", turn.Select(m => $"{m.Role}: {m.Text}"));
        var response = await _agent.RunAsync(transcript, cancellationToken: cancellationToken);
        return ParseFacts(response.Text);
    }

    /// <summary>
    /// Tolerant parser for the extractor's reply: finds the innermost slice that
    /// deserializes as a JSON string array — prose brackets ("Okay [1 fact]: …",
    /// "… [end]") no longer corrupt the slice, because candidate <c>[ ... ]</c>
    /// spans are tried from the most-plausible (last opener with the last closer)
    /// outward until one parses. Malformed output, non-string items, or an empty
    /// response all yield an empty list — never an exception.
    /// </summary>
    public static IReadOnlyList<string> ParseFacts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        for (var end = text.LastIndexOf(']'); end > 0; end = text.LastIndexOf(']', end - 1))
        {
            for (var start = text.LastIndexOf('[', end); start >= 0;
                 start = start > 0 ? text.LastIndexOf('[', start - 1) : -1)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<string[]>(text[start..(end + 1)]);
                    if (parsed is null)
                    {
                        return [];
                    }

                    var facts = parsed
                        .Where(f => !string.IsNullOrWhiteSpace(f))
                        .Select(f => f.Trim())
                        .ToArray();
                    if (facts.Length > 0)
                    {
                        return facts;
                    }
                }
                catch (JsonException)
                {
                    // This candidate span is not the array — try the next.
                }
            }
        }

        return [];
    }
}
