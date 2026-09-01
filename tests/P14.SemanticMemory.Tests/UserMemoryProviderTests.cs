using MafDemo.Core.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using P14.SemanticMemory.Memory;

namespace P14.SemanticMemory.Tests;

/// <summary>
/// Offline gate for <see cref="UserMemoryProvider"/>: the Store path must parse
/// the extractor's JSON string[] and add facts to the store; empty arrays,
/// malformed JSON, non-external messages, and failing extractors are all
/// no-ops that never throw. The recall (Provide) path must inject the recalled
/// fact text as context. No model, no network — the extractor is a fake, the
/// embedder is a deterministic table (same pattern as FactMemoryStoreTests).
/// </summary>
public class UserMemoryProviderTests
{
    private const int Dims = 8;
    private const string FactText = "User prefers email over phone";
    private const string RecallQuery = "How should we contact you about my ticket?";
    private const string OtherFact = "User lives in Paris";

    private static FakeEmbedder CreateEmbedder() => new(new Dictionary<string, float[]>
    {
        [FactText] = OneHot(0),
        [RecallQuery] = OneHot(0), // the recall question maps to the fact's vector
        [OtherFact] = OneHot(1),
        ["Remember: I prefer email over phone."] = OneHot(1),
    });

    private static FactMemoryStore CreateStore() => new(CreateEmbedder());

    private static UserMemoryProvider CreateProvider(FactMemoryStore facts, IFactExtractor extractor, string? userId = null) =>
        new(facts, userId: userId, extractor: extractor);

    private static AIContextProvider.InvokedContext CreateInvokedContext(IReadOnlyList<ChatMessage> requestMessages) =>
        new(TestAgent.Instance, session: null, requestMessages, [new ChatMessage(ChatRole.Assistant, "Got it.")]);

    private static AIContextProvider.InvokingContext CreateInvokingContext(string userText) =>
        new(TestAgent.Instance, session: null, new AIContext { Messages = [new ChatMessage(ChatRole.User, userText)] });

    [Fact]
    public async Task StoreAIContextAsync_parses_extractor_json_and_adds_fact()
    {
        var store = CreateStore();
        var provider = CreateProvider(store, new ScriptedExtractor(["User prefers email over phone"]));

        await provider.InvokedAsync(CreateInvokedContext(
            [new ChatMessage(ChatRole.User, "Remember: I prefer email over phone.")]));

        // Recall gate: the stored fact is found by the store itself, user-scoped.
        var recalled = await store.RecallAsync(UserMemoryProvider.DefaultUserId, RecallQuery);
        var fact = Assert.Single(recalled);
        Assert.Contains("email", fact.Text);
    }

    [Fact]
    public async Task StoreAIContextAsync_with_empty_array_adds_nothing()
    {
        var store = CreateStore();
        var provider = CreateProvider(store, new ScriptedExtractor([]));

        await provider.InvokedAsync(CreateInvokedContext(
            [new ChatMessage(ChatRole.User, "Remember: I prefer email over phone.")]));

        Assert.Empty(await store.RecallAsync(UserMemoryProvider.DefaultUserId, RecallQuery));
    }

    [Fact]
    public async Task StoreAIContextAsync_with_malformed_json_is_a_noop_not_a_throw()
    {
        var store = CreateStore();
        // The scripted extractors mimic the real one: raw model text goes
        // through ChatClientFactExtractor.ParseFacts. Truncated JSON and
        // prose-with-no-array both parse to "no facts".
        var truncated = new ScriptedExtractor(ChatClientFactExtractor.ParseFacts("[\"User prefers email"));
        var prose = new ScriptedExtractor(ChatClientFactExtractor.ParseFacts("I would say the user likes email."));

        await CreateProvider(store, truncated).InvokedAsync(CreateInvokedContext(
            [new ChatMessage(ChatRole.User, "Remember: I prefer email over phone.")]));

        await CreateProvider(store, prose).InvokedAsync(CreateInvokedContext(
            [new ChatMessage(ChatRole.User, "Remember: I prefer email over phone.")]));

        Assert.Empty(await store.RecallAsync(UserMemoryProvider.DefaultUserId, RecallQuery));
    }

    [Fact]
    public async Task StoreAIContextAsync_with_failing_extractor_does_not_throw()
    {
        var store = CreateStore();
        var provider = CreateProvider(store, new ThrowingExtractor());

        var exception = await Record.ExceptionAsync(async () => await provider.InvokedAsync(CreateInvokedContext(
            [new ChatMessage(ChatRole.User, "Remember: I prefer email over phone.")])));

        Assert.Null(exception);
        Assert.Empty(await store.RecallAsync(UserMemoryProvider.DefaultUserId, RecallQuery));
    }

    [Fact]
    public async Task StoreAIContextAsync_ignores_non_external_messages()
    {
        var store = CreateStore();
        var provider = CreateProvider(store, new ScriptedExtractor(["User prefers email over phone"]));

        // A message stamped as produced by another context provider is not
        // external input and must never be mined for facts.
        var injected = new ChatMessage(ChatRole.User, "Remember: I prefer email over phone.")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "other-provider");

        await provider.InvokedAsync(CreateInvokedContext([injected]));

        Assert.Empty(await store.RecallAsync(UserMemoryProvider.DefaultUserId, RecallQuery));
    }

    [Fact]
    public async Task StoreAIContextAsync_scopes_facts_to_the_provider_user()
    {
        var store = CreateStore();
        var provider = CreateProvider(store, new ScriptedExtractor(["User prefers email over phone"]), userId: "u2");

        await provider.InvokedAsync(CreateInvokedContext(
            [new ChatMessage(ChatRole.User, "Remember: I prefer email over phone.")]));

        Assert.Empty(await store.RecallAsync("u1", RecallQuery));
        _ = Assert.Single(await store.RecallAsync("u2", RecallQuery));
    }

    [Fact]
    public async Task ProvideAIContextAsync_injects_recalled_facts_as_context()
    {
        var store = CreateStore();
        await store.AddAsync(UserMemoryProvider.DefaultUserId, FactText);
        var provider = CreateProvider(store, new ScriptedExtractor([]));

        var context = await provider.InvokingAsync(CreateInvokingContext(RecallQuery));

        // InvokingAsync returns the MERGED context: the caller's input plus the
        // provider's injected memory message (stamped with the provider source).
        var messages = context.Messages!.ToList();
        Assert.Equal(2, messages.Count);
        var memory = messages[1];
        Assert.Contains("email", memory.Text);
        Assert.Equal(AgentRequestMessageSourceType.AIContextProvider,
            memory.GetAgentRequestMessageSourceType());
    }

    [Fact]
    public async Task ProvideAIContextAsync_on_empty_store_returns_empty_context()
    {
        var provider = CreateProvider(CreateStore(), new ScriptedExtractor([]));

        var context = await provider.InvokingAsync(CreateInvokingContext(RecallQuery));

        // Only the caller's input — no memory message was injected.
        var message = Assert.Single(context.Messages!);
        Assert.Equal(RecallQuery, message.Text);
    }

    [Fact]
    public async Task ProvideAIContextAsync_with_failing_backend_does_not_throw()
    {
        // The store itself is fine; its embedder throws, so RecallAsync throws.
        // The provider must degrade to "no memory context", not break the run.
        var provider = new UserMemoryProvider(new FactMemoryStore(new ThrowingEmbedder()), extractor: new ScriptedExtractor([]));

        var context = await provider.InvokingAsync(CreateInvokingContext(RecallQuery));

        var message = Assert.Single(context.Messages!);
        Assert.Equal(RecallQuery, message.Text);
    }

    [Fact]
    public void ParseFacts_parses_plain_and_fenced_arrays()
    {
        Assert.Equal(["User prefers email over phone"],
            ChatClientFactExtractor.ParseFacts("""["User prefers email over phone"]"""));

        var wrapped = ChatClientFactExtractor.ParseFacts(
            """Here you go: ["User prefers email over phone", "User works nights"] thanks""");
        Assert.Equal(2, wrapped.Count);
        Assert.Contains("email", wrapped[0]);

        var fenced = ChatClientFactExtractor.ParseFacts(
            "```json\n[\"User prefers email over phone\"]\n```");
        Assert.Equal(["User prefers email over phone"], fenced);
    }

    [Theory]
    [InlineData("plain prose, no array at all")]
    [InlineData("")]
    [InlineData("[\"truncated, no closing bracket")]
    [InlineData("[42, true]")]
    [InlineData("[\"\", \"   \"]")]
    [InlineData("[]")]
    public void ParseFacts_treats_unparseable_or_empty_replies_as_no_facts(string reply)
    {
        Assert.Empty(ChatClientFactExtractor.ParseFacts(reply));
    }

    // ---- helpers ----

    private static float[] OneHot(int index)
    {
        var v = new float[Dims];
        v[index] = 1f;
        return v;
    }

    /// <summary>
    /// Minimal AIAgent for constructing provider contexts; the wrapped client
    /// throws if ever used, which would mean the provider talked to a model.
    /// </summary>
    private static class TestAgent
    {
        public static readonly ChatClientAgent Instance = new(new NeverUsedChatClient());
    }

    private sealed class NeverUsedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unit tests must not invoke a chat model");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unit tests must not invoke a chat model");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Canned extractor: returns the given facts regardless of input.</summary>
    private sealed class ScriptedExtractor(IReadOnlyList<string> facts) : IFactExtractor
    {
        public Task<IReadOnlyList<string>> ExtractFactsAsync(IReadOnlyList<ChatMessage> turn, CancellationToken cancellationToken = default) =>
            Task.FromResult(facts);
    }

    private sealed class ThrowingExtractor : IFactExtractor
    {
        public Task<IReadOnlyList<string>> ExtractFactsAsync(IReadOnlyList<ChatMessage> turn, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("model down");
    }

    private sealed class ThrowingEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("embedding backend down");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// Offline fake: same string always yields the same vector (table lookup).
    /// Unknown strings fall back to a one-hot drawn only from indices no table
    /// vector occupies, so an unknown input can never score cosine 1.0 against
    /// a known one and accidentally trigger dedupe-upsert (same pattern as
    /// FactMemoryStoreTests in tests/MafDemo.Core.Tests).
    /// </summary>
    private sealed class FakeEmbedder(IReadOnlyDictionary<string, float[]> table) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly int[] _freeIndices = Enumerable.Range(0, Dims)
            .Except(table.Values.Select(v => Array.FindIndex(v, static x => x != 0)).Where(i => i >= 0))
            .ToArray();

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var embeddings = values.Select(text =>
            {
                if (table.TryGetValue(text, out var known))
                {
                    return new Embedding<float>(known);
                }

                if (_freeIndices.Length == 0)
                {
                    return new Embedding<float>(new float[Dims]);
                }

                var index = _freeIndices[(uint)text.GetHashCode() % _freeIndices.Length];
                return new Embedding<float>(OneHot(index));
            });
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
