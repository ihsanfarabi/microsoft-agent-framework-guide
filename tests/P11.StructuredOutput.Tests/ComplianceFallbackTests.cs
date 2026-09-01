using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MafDemo.Core.Domain;
using P11.StructuredOutput;

namespace P11.StructuredOutput.Tests;

/// <summary>
/// Unit tests for <see cref="ComplianceFallback.RunJsonWithFallbackAsync{T}"/>:
/// a scripted fake <see cref="IChatClient"/> returns a broken, prose-wrapped
/// response first; the fallback must re-prompt once (schema embedded in the
/// prompt) and parse the fenced JSON the retry returns.
/// </summary>
public class ComplianceFallbackTests
{
    [Fact]
    public async Task RunJsonWithFallbackAsync_reprompts_once_and_parses_fenced_retry()
    {
        var client = new ScriptedClient(
            // Call 1: prose around a fenced JSON object with an out-of-range enum —
            // a cloud model "ignoring" the ResponseFormat schema.
            """
            Sure! Here's my triage: ```json
            {"Category":"Hardware","Priority":99,"Summary":"priority number made up"}
            ``` hope that helps!
            """,
            // Call 2 (the re-prompt): fenced but otherwise valid JSON.
            """
            ```json
            {"Category":"Hardware","Priority":"High","Summary":"laptop won't boot"}
            ```
            """);
        var agent = NewAgent(client);
        var opts = new ChatClientAgentRunOptions();

        TriageDecision? decision = await ComplianceFallback.RunJsonWithFallbackAsync<TriageDecision>(
            agent, "Laptop won't boot, deadline tomorrow", opts);

        Assert.NotNull(decision);
        Assert.Equal(TicketCategory.Hardware, decision.Category);
        Assert.Equal(TicketPriority.High, decision.Priority);
        Assert.Equal("laptop won't boot", decision.Summary);
        Assert.Equal(2, client.Calls);

        // The retry prompt must embed the JSON schema text, per the fallback contract.
        string retryPrompt = client.SecondCallUserText;
        Assert.Contains("JSON Schema", retryPrompt);
        Assert.Contains("\"type\"", retryPrompt);

        // Both calls ran with a per-call schema format naming T (the non-generic
        // RunAsync form — the only one where the caller's format survives).
        Assert.All(client.SeenSchemaNames, name => Assert.Equal("TriageDecision", name));
    }

    [Fact]
    public async Task RunJsonWithFallbackAsync_returns_default_when_both_calls_fail()
    {
        var client = new ScriptedClient("not json at all", "still not json");
        var agent = NewAgent(client);

        TicketDraft? draft = await ComplianceFallback.RunJsonWithFallbackAsync<TicketDraft>(
            agent, "draft a ticket", new ChatClientAgentRunOptions());

        Assert.Null(draft);
        Assert.Equal(2, client.Calls); // re-prompts exactly once, then gives up
    }

    private static ChatClientAgent NewAgent(IChatClient client) =>
        new(client, new ChatClientAgentOptions { Name = "ScriptedBot" });

    /// <summary>
    /// Fake <see cref="IChatClient"/> that replays scripted responses in order
    /// (the last one repeats if more calls arrive) and records what it saw, so
    /// tests can assert on the per-call format and the retry prompt.
    /// </summary>
    private sealed class ScriptedClient(params string[] responses) : IChatClient
    {
        private int _index;

        public int Calls => _index;

        public List<ChatResponseFormat?> SeenFormats { get; } = [];

        public IEnumerable<string> SeenSchemaNames =>
            SeenFormats.OfType<ChatResponseFormatJson>().Where(f => f.Schema is not null)
                .Select(f => f.SchemaName ?? "");

        public string SecondCallUserText =>
            Messages.ElementAtOrDefault(1)?.LastOrDefault()?.Text ?? "";

        private List<IEnumerable<ChatMessage>> Messages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages.Add(messages);
            SeenFormats.Add(options?.ResponseFormat);
            string text = _index < responses.Length ? responses[_index] : responses[^1];
            _index++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("P11's fallback is non-streaming");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
