using System.Text.Json;
using System.Text.Json.Serialization;
using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P11.StructuredOutput;

/// <summary>The support-ticket category the triage agent assigns.</summary>
public enum TicketCategory { Hardware, Network, Account, Security, Other }

/// <summary>Typed output of the triage agent, deserialized from the model's JSON response.</summary>
public record TriageDecision(TicketCategory Category, TicketPriority Priority, string Summary);

/// <summary>
/// Raw-path target: a support-ticket draft the caller requests with a per-call
/// <see cref="ChatResponseFormat"/> carrying a hand-built JSON schema, then
/// deserializes itself — no <c>RunAsync&lt;T&gt;</c> involved.
/// </summary>
public record TicketDraft(string Title, TicketPriority Priority, string Description);

/// <summary>
/// P11 baseline: a <see cref="ChatClientAgent"/> whose responses are deserialized
/// into <see cref="TriageDecision"/> via <c>RunAsync&lt;T&gt;</c>, plus a
/// model-free probe that validates JSON text the same way the typed run's
/// deserializer would.
/// </summary>
public static class TypedTriage
{
    /// <summary>
    /// Case-insensitive, enum-tolerant options: <see cref="JsonStringEnumConverter"/>
    /// accepts both string names ("Hardware") and integer values (2). Shared by
    /// <c>RunAsync&lt;T&gt;</c> (whose built-in deserializer is case-sensitive by
    /// default, and Ollama models emit camelCase) and <see cref="ProbeJson"/>, so
    /// the probe validates exactly what the typed run accepts.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Outcome of probing raw text for deserializability as <see cref="TriageDecision"/>.</summary>
    public record ComplianceProbe(bool Ok, string Raw, string Error = "");

    /// <summary>
    /// Attempts to deserialize <paramref name="text"/> as a <see cref="TriageDecision"/>
    /// without calling a model. Returns Ok=false with the failure reason when the text
    /// is not valid JSON, has the wrong shape, or carries an out-of-range enum value.
    /// Markdown fences are stripped first, mirroring what the typed run tolerates.
    /// </summary>
    public static ComplianceProbe ProbeJson(string text)
    {
        try
        {
            var decision = JsonSerializer.Deserialize<TriageDecision>(NormalizeJsonText(text), JsonOptions);
            if (decision is null)
                return new(false, text, "deserialized to null");
            if (!Enum.IsDefined(decision.Category))
                return new(false, text, $"unknown category value: {decision.Category}");
            if (!Enum.IsDefined(decision.Priority))
                return new(false, text, $"unknown priority value: {decision.Priority}");
            return new(true, text);
        }
        catch (JsonException ex)
        {
            return new(false, text, ex.Message);
        }
    }

    /// <summary>
    /// Triage agent wired to the given chat client. Instructing "JSON only" keeps
    /// the raw response parseable so <c>RunAsync&lt;T&gt;</c> can deserialize it.
    /// <c>RunAsync&lt;T&gt;</c> attaches a JSON-schema <c>ResponseFormat</c> itself;
    /// but Ollama applies that constraint only best-effort for cloud-routed models,
    /// so the client is wrapped to strip markdown fences the model may still emit.
    /// </summary>
    public static ChatClientAgent TypedTriageAgent(IChatClient client) =>
        new(new JsonFenceCoercionClient(client), new ChatClientAgentOptions
        {
            Name = "TriageBot",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    """
                    Classify the user's ticket. Respond with JSON only — no markdown fences,
                    no commentary. Use exactly these fields and values:
                    "Category": one of Hardware, Network, Account, Security, Other.
                    "Priority": one of Low, Normal, High, Critical.
                    "Summary": a short one-sentence description of the issue.
                    """,
                ResponseFormat = ChatResponseFormat.Json,
            },
        });

    /// <summary>
    /// Removes a markdown code fence (``` / ```json) and its closing fence
    /// wherever they occur in the response, keeping only the body — which
    /// <see cref="AgentResponse{T}.Result"/>'s deserializer can read. Cloud-routed
    /// models often prepend a sentence ("Sure! Here is the classification:") before
    /// the fence, so gating on "response starts with ```" leaves exactly those
    /// cases broken. Text with no fence passes through unchanged.
    /// </summary>
    public static string NormalizeJsonText(string text)
    {
        var trimmed = text.Trim();
        int open = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (open < 0)
            return text;

        // The fence may be "```json\n" — the body starts after the fence's line.
        int firstNewline = trimmed.IndexOf('\n', open);
        if (firstNewline < 0)
            return text;

        var body = trimmed[(firstNewline + 1)..];
        int closing = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
            body = body[..closing];
        return body;
    }

    /// <summary>
    /// IChatClient middleware that rewrites fenced-model responses into bare JSON
    /// before they reach the typed-run deserializer. Non-streaming only; streaming
    /// passes through (P11 uses the non-streaming typed run).
    /// </summary>
    private sealed class JsonFenceCoercionClient(IChatClient inner) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            if (response.Text is { } text && text.Contains("```", StringComparison.Ordinal))
            {
                foreach (var message in response.Messages)
                {
                    if (!message.Text.Contains("```", StringComparison.Ordinal))
                        continue;
                    message.Contents =
                    [
                        new TextContent(NormalizeJsonText(message.Text)),
                        .. message.Contents.Where(c => c is not TextContent),
                    ];
                }
            }
            return response;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();
    }
}
