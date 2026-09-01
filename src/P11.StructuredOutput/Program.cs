using System.Text.Json;
using MafDemo.AgentCommon;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using P11.StructuredOutput;

// Start OTel tracing first so the provider is listening before any model call.
// Disposed on exit, which flushes the spans to the console exporter.
using var telemetry = Telemetry.Start("P11.StructuredOutput");

const string TicketMessage = "Laptop won't boot, deadline tomorrow";

// Recorder sits between Ollama and the agent so each path can report the
// ResponseFormat that actually reached the model — per-call formats can be
// overridden by MAF (see the NOTE under the per-call options path).
var recorder = new ResponseFormatRecorder(OllamaChat.Create());
var agent = TypedTriage.TypedTriageAgent(recorder);

// Path 1: typed run. MAF itself attaches ChatResponseFormat.ForJsonSchema<T>()
// and deserializes the response into TriageDecision — no manual JSON handling.
Console.WriteLine("=== Path 1: typed RunAsync<TriageDecision> (schema attached by MAF) ===");
try
{
    TriageDecision decision = await FormatPathDemos.RunTypedAsync(agent, TicketMessage);
    Console.WriteLine($"Category: {decision.Category}");
    Console.WriteLine($"Priority: {decision.Priority}");
    Console.WriteLine($"Summary:  {decision.Summary}");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
}
Console.WriteLine($"Model received: {FormatPathDemos.DescribeFormat(recorder.LastResponseFormat)}");
Console.WriteLine();

// Path 2: per-call options. The format is passed explicitly as
// ChatClientAgentRunOptions on a typed RunAsync<T>. Because RunAsync<T> always
// injects its own ForJsonSchema<T>, the per-call format carries a distinctive
// schema name ("PerCallTriageDecision") so the program can detect and report
// whether it survived to the model.
Console.WriteLine("=== Path 2: per-call ChatClientAgentRunOptions with ForJsonSchema<TriageDecision>() ===");
try
{
    TriageDecision decision = await FormatPathDemos.RunPerCallOptionsAsync(agent, TicketMessage);
    Console.WriteLine($"Category: {decision.Category}");
    Console.WriteLine($"Priority: {decision.Priority}");
    Console.WriteLine($"Summary:  {decision.Summary}");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
}
bool perCallSurvived = recorder.LastResponseFormat is ChatResponseFormatJson { SchemaName: "PerCallTriageDecision" };
Console.WriteLine($"Model received: {FormatPathDemos.DescribeFormat(recorder.LastResponseFormat)}");
Console.WriteLine(perCallSurvived
    ? "NOTE: the per-call ResponseFormat reached the model."
    : "NOTE: the per-call ResponseFormat was overridden — RunAsync<T> always injects its own ForJsonSchema<T>.");
Console.WriteLine();

// Path 3: raw run. The schema is built by hand (AIJsonUtilities.CreateJsonSchema,
// since this package line has no JsonSchema.For<T>()), passed on a NON-typed
// RunAsync, and the caller owns both the response text and the deserialization.
Console.WriteLine("=== Path 3: raw RunAsync + hand-built schema (TicketDraft), manual deserialize ===");
try
{
    (AgentResponse response, TicketDraft? draft, string? parseError) =
        await FormatPathDemos.ExtractRawAsync(agent, TicketMessage);
    Console.WriteLine($"Raw text: {response.Text}");
    if (draft is not null)
    {
        Console.WriteLine($"Title:       {draft.Title}");
        Console.WriteLine($"Priority:    {draft.Priority}");
        Console.WriteLine($"Description: {draft.Description}");
    }
    if (parseError is not null)
        Console.WriteLine($"Manual deserialize FAILED: {parseError}");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
}
Console.WriteLine($"Model received: {FormatPathDemos.DescribeFormat(recorder.LastResponseFormat)}");

/// <summary>
/// Program-internal demo methods for the three response-format paths.
/// Each performs exactly one agent run; printing stays in the top-level program.
/// </summary>
internal static class FormatPathDemos
{
    // Per-call redirection for the raw path: the agent's standing instructions
    // describe the triage shape (Category/Priority/Summary), so a per-call
    // instruction restates the draft shape the TicketDraft schema asks for.
    // ChatClientAgent appends per-run instructions after the agent's own.
    private const string DraftInstructions =
        """
        You are drafting a NEW support ticket, not classifying one. Respond with a JSON
        object with exactly these fields: "Title" (short ticket title), "Priority"
        (one of Low, Normal, High, Critical), "Description" (one-sentence description).
        """;

    /// <summary>Path 1: typed run — MAF attaches ForJsonSchema&lt;T&gt; and deserializes.</summary>
    public static async Task<TriageDecision> RunTypedAsync(AIAgent agent, string message) =>
        (await agent.RunAsync<TriageDecision>(
            message,
            serializerOptions: TypedTriage.JsonOptions)).Result;

    /// <summary>Path 2: typed run with an explicit per-call ResponseFormat.</summary>
    public static async Task<TriageDecision> RunPerCallOptionsAsync(AIAgent agent, string message) =>
        (await agent.RunAsync<TriageDecision>(
            message,
            serializerOptions: TypedTriage.JsonOptions,
            options: new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.ForJsonSchema<TriageDecision>(
                        schemaName: "PerCallTriageDecision"),
                },
            })).Result;

    /// <summary>
    /// Path 3: raw run with a hand-built TicketDraft schema on a non-typed
    /// RunAsync. Returns the raw response plus the manually deserialized draft
    /// (<c>null</c> with the failure reason when the text did not comply).
    /// </summary>
    public static async Task<(AgentResponse Response, TicketDraft? Draft, string? ParseError)>
        ExtractRawAsync(AIAgent agent, string message)
    {
        JsonElement schema = AIJsonUtilities.CreateJsonSchema(typeof(TicketDraft));
        AgentResponse response = await agent.RunAsync(
            message,
            options: new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = DraftInstructions,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(
                        schema, "TicketDraft", "A new support-ticket draft"),
                },
            });

        try
        {
            TicketDraft? draft = JsonSerializer.Deserialize<TicketDraft>(
                TypedTriage.NormalizeJsonText(response.Text ?? string.Empty), TypedTriage.JsonOptions);
            return (response, draft, draft is null ? "deserialized to null" : null);
        }
        catch (JsonException ex)
        {
            return (response, null, ex.Message);
        }
    }

    /// <summary>One-line description of the format that reached the model.</summary>
    public static string DescribeFormat(ChatResponseFormat? format) => format switch
    {
        null => "(none)",
        ChatResponseFormatJson { Schema: { ValueKind: JsonValueKind.Object } } schema
            => $"JsonSchema(name={schema.SchemaName ?? "(unnamed)"}, schema={SummarizeSchema(schema.Schema.Value)})",
        ChatResponseFormatJson => "Json (bare JSON object, no schema)",
        _ => format.GetType().Name,
    };

    private static string SummarizeSchema(JsonElement schema)
    {
        string text = schema.GetRawText();
        return text.Length <= 110 ? text : text[..107] + "...";
    }
}

/// <summary>
/// Demo instrumentation: captures the last ResponseFormat handed to the model so
/// the program can report what actually arrived (per-call formats can be
/// overridden — see the Path 2 NOTE). Everything else passes through untouched.
/// </summary>
internal sealed class ResponseFormatRecorder(IChatClient inner) : IChatClient
{
    public ChatResponseFormat? LastResponseFormat { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastResponseFormat = options?.ResponseFormat;
        return await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
