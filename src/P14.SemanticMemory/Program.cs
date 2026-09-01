using CommunityToolkit.VectorData.InMemory;
using MafDemo.AgentCommon;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using OllamaSharp;
using P14.SemanticMemory;

// Start OTel tracing first so the provider is listening before any model call.
using var telemetry = Telemetry.Start("P14.SemanticMemory");

// The embedding generator is attached to the vector store itself, so every
// message the memory provider upserts (and every search query) is embedded
// with bge-m3 automatically. OllamaApiClient implements both IChatClient and
// IEmbeddingGenerator, so the interface cast picks the embedding surface.
IEmbeddingGenerator<string, Embedding<float>> embedder = OllamaEmbedding.Create();

// Task 1 baseline is an in-process vector store: memories live for the
// lifetime of the process only. Cross-SESSION recall works (a new
// AgentSession finds messages stored under another session, because the
// search scope is the user id, not the session id), but cross-PROCESS
// restart does not — `dotnet run -- tell` followed by `dotnet run -- recall`
// in a fresh process finds an empty store. A durable store is the natural
// Task 2/3 upgrade and is deliberately out of scope here.
var vectorStore = new InMemoryVectorStore(new InMemoryVectorStoreOptions
{
    EmbeddingGenerator = embedder,
});

// Real API (Microsoft.Agents.AI 1.19.0) differs from the research notes the
// plan was written from: scopes are set via a state-initializer delegate on
// the constructor — there is no fluent `.State(...)` method, no `Search`
// property on the scope (scoping IS the scope object: fields left null span
// everything), and the vector dimensions move from the state call into the
// ctor. Both scopes pin only the UserId, deliberately omitting SessionId —
// that omission is what makes memory cross sessions.
var memory = new ChatHistoryMemoryProvider(
    vectorStore,
    collectionName: "p14-chat-history",
    vectorDimensions: MemoryFacts.VectorDimensions,
    stateInitializer: _ => new ChatHistoryMemoryProvider.State(
        // (storageScope, searchScope) — both pinned to the UserId only; the
        // SessionId field is deliberately left null so recall crosses sessions.
        new ChatHistoryMemoryProviderScope { UserId = "demo-user" },
        new ChatHistoryMemoryProviderScope { UserId = "demo-user" }));

var agent = new ChatClientAgent(OllamaChat.Create(), new ChatClientAgentOptions
{
    Name = "MemoryBot",
    Description = "A helpful assistant that remembers prior conversations.",
    ChatOptions = new ChatOptions
    {
        Instructions = "Answer briefly. Use remembered context from earlier conversations when it is relevant.",
    },
    AIContextProviders = [memory],
});

// Modes:
//   (no args)  scripted two-session demo in one process: tell in session A,
//              then ask the recall question in a brand-new session B.
//   tell       run only the "tell" phase, then exit (second process run).
//   recall     run only the "recall" phase (fresh process, empty store).
switch (args.FirstOrDefault())
{
    case null:
        await ScriptedDemoAsync(agent);
        break;
    case "tell":
        await TellAsync(agent);
        break;
    case "recall":
        await RecallAsync(agent);
        break;
    default:
        Console.WriteLine("usage: dotnet run [-- tell|recall]  (no args = scripted demo)");
        return;
}

// Session 1 states a preference; session 2 asks a question that only makes
// sense if the memory provider injected session 1's messages as context.
static async Task ScriptedDemoAsync(ChatClientAgent agent)
{
    AgentSession sessionA = await agent.CreateSessionAsync();
    Console.WriteLine("== session A (new) ==");
    Console.WriteLine("user> Remember: I prefer email over phone.");
    var tell = await agent.RunAsync("Remember: I prefer email over phone.", sessionA);
    Console.WriteLine($"bot> {tell.Text}");

    // A different session id — same process, but a fresh conversation with
    // no shared chat history; only the vector-backed memory provider can
    // carry the preference across.
    AgentSession sessionB = await agent.CreateSessionAsync();
    Console.WriteLine("== session B (new) ==");
    Console.WriteLine("user> How should we contact you about my ticket?");
    var recall = await agent.RunAsync("How should we contact you about my ticket?", sessionB);
    Console.WriteLine($"bot> {recall.Text}");
}

static async Task TellAsync(ChatClientAgent agent)
{
    AgentSession session = await agent.CreateSessionAsync();
    Console.WriteLine("== session A (new) ==");
    Console.WriteLine("user> Remember: I prefer email over phone.");
    var tell = await agent.RunAsync("Remember: I prefer email over phone.", session);
    Console.WriteLine($"bot> {tell.Text}");
    Console.WriteLine("(process exiting — InMemoryVectorStore contents are gone)");
}

static async Task RecallAsync(ChatClientAgent agent)
{
    AgentSession session = await agent.CreateSessionAsync();
    Console.WriteLine("== session B (new) ==");
    Console.WriteLine("user> How should we contact you about my ticket?");
    var recall = await agent.RunAsync("How should we contact you about my ticket?", session);
    Console.WriteLine($"bot> {recall.Text}");
}
