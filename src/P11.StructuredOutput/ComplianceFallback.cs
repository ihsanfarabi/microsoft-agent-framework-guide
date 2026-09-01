using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P11.StructuredOutput;

/// <summary>
/// Cloud-reality fallback: run with a per-call JSON-schema <see cref="ChatResponseFormat"/>,
/// probe the raw response, and if the model ignored the schema (Ollama applies it only
/// best-effort for cloud-routed models — ollama/ollama#12362), re-prompt ONCE with the
/// JSON schema embedded in the prompt text. Returns <c>default</c> when both calls fail.
/// </summary>
/// <remarks>
/// Uses the NON-generic <see cref="AIAgent.RunAsync(string, Microsoft.Agents.AI.AgentRunOptions?, System.Threading.CancellationToken)"/>:
/// <c>RunAsync&lt;T&gt;</c> unconditionally injects its own <c>ForJsonSchema&lt;T&gt;</c> and
/// discards any per-call format, while on this form the caller's
/// <see cref="ChatOptions.ResponseFormat"/> demonstrably reaches the model (P11 path 3).
/// Validation reuses <see cref="TypedTriage.ProbeJson"/> for <see cref="TriageDecision"/> and
/// a generic probe (same options, same fence stripping via
/// <see cref="TypedTriage.NormalizeJsonText"/>) for every other target type.
/// </remarks>
public static class ComplianceFallback
{
    /// <summary>
    /// Runs <paramref name="agent"/> once with a schema response format, validates the raw
    /// text as <typeparamref name="T"/>, and on non-compliance re-prompts exactly once with
    /// the JSON schema embedded in the prompt. Tolerant of markdown fences. Returns the
    /// typed result, or <c>default</c> when both attempts fail to produce valid JSON.
    /// </summary>
    /// <param name="opts">Per-call run options; its <see cref="ChatOptions"/> (instructions
    /// etc.) are preserved, with <see cref="ChatOptions.ResponseFormat"/> set to the schema
    /// for <typeparamref name="T"/>. The caller's options object is not mutated.</param>
    public static async Task<T?> RunJsonWithFallbackAsync<T>(
        AIAgent agent, string message, ChatClientAgentRunOptions opts)
    {
        JsonElement schema = AIJsonUtilities.CreateJsonSchema(typeof(T));
        ChatClientAgentRunOptions schemaOpts = WithSchemaFormat(opts, schema, typeof(T).Name);

        AgentResponse first = await agent.RunAsync(message, options: schemaOpts).ConfigureAwait(false);
        ProbeResult<T> probe = Probe<T>(first.Text ?? string.Empty);
        if (probe.Ok)
            return probe.Value;

        string retryMessage =
            $"""
            {message}

            Your previous reply was not valid for the required output format. Reason: {probe.Error}
            Try again. Respond with ONLY a JSON object — no markdown fences, no commentary —
            that validates against this JSON Schema:

            {schema.GetRawText()}
            """;
        AgentResponse retry = await agent.RunAsync(retryMessage, options: schemaOpts).ConfigureAwait(false);
        probe = Probe<T>(retry.Text ?? string.Empty);
        return probe.Ok ? probe.Value : default;
    }

    private static ChatClientAgentRunOptions WithSchemaFormat(
        ChatClientAgentRunOptions opts, JsonElement schema, string name)
    {
        var chatOptions = opts.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, name);
        return new ChatClientAgentRunOptions { ChatOptions = chatOptions };
    }

    private record ProbeResult<T>(bool Ok, T Value, string Error = "");

    /// <summary>
    /// Tolerant parse of raw model text as <typeparamref name="T"/>: strips markdown
    /// fences, then deserializes with <see cref="TypedTriage.JsonOptions"/> (case-insensitive,
    /// enum-as-string). Delegates to <see cref="TypedTriage.ProbeJson"/> for
    /// <see cref="TriageDecision"/> so the probe and the typed run stay one contract.
    /// </summary>
    private static ProbeResult<T> Probe<T>(string text)
    {
        if (typeof(T) == typeof(TriageDecision))
        {
            TypedTriage.ComplianceProbe decision = TypedTriage.ProbeJson(text);
            if (!decision.Ok)
                return new(false, default!, decision.Error);
            var value = JsonSerializer.Deserialize<TriageDecision>(
                TypedTriage.NormalizeJsonText(text), TypedTriage.JsonOptions);
            return value is null
                ? new(false, default!, "deserialized to null")
                : new(true, (T)(object)value);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(
                TypedTriage.NormalizeJsonText(text), TypedTriage.JsonOptions);
            if (value is null)
                return new(false, default!, "deserialized to null");
            string? enumError = EnumRangeError(value);
            return enumError is null
                ? new(true, value)
                : new(false, default!, enumError);
        }
        catch (JsonException ex)
        {
            return new(false, default!, ex.Message);
        }
    }

    /// <summary>
    /// Generic check the shared probe only does for <see cref="TriageDecision"/>: any enum
    /// property holding a value outside its defined names (an integer like 99 slips through
    /// <see cref="JsonStringEnumConverter"/>'s number reading) fails the probe.
    /// </summary>
    private static string? EnumRangeError<T>(T value)
    {
        foreach (var property in typeof(T).GetProperties())
        {
            if (!property.PropertyType.IsEnum || property.GetIndexParameters().Length > 0)
                continue;
            object? raw = property.GetValue(value);
            if (raw is null || !Enum.IsDefined(property.PropertyType, raw))
                return $"unknown {property.Name} value: {raw}";
        }
        return null;
    }
}
