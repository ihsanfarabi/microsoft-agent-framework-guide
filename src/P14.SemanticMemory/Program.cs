using System.Globalization;
using CommunityToolkit.VectorData.InMemory;
using MafDemo.AgentCommon;
using MafDemo.Core.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using OllamaSharp;
using P14.SemanticMemory;
using P14.SemanticMemory.Memory;

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

// ---- Task 3: fact-extraction memory, running alongside the T1 baseline ----
// A second AIContextProvider with a different memory strategy: after each run
// a tiny extractor agent turns the conversation turn into durable third-person
// facts ("User prefers email over phone"), which are upserted into a
// FactMemoryStore (dedupe — cosine >= 0.9, user-scoped — is owned by the
// store, not the provider); before each run the facts most similar to the
// latest user message are injected as a system message. Unlike the
// chat-history collection above, the fact store persists to a JSON file, so
// `dotnet run -- tell` followed by `dotnet run -- recall` in a fresh process
// DOES recall — exactly where the T1 baseline cannot.
const string factsPath = "p14-facts.json";
var factStore = new FactMemoryStore(embedder);
// Load persisted facts. LoadAsync itself quarantines a corrupt file (moved to
// <path>.corrupt) and starts empty, so TryLoadAsync is the outer guard for
// anything else that can go wrong reading the file (permissions, IO errors):
// warned about and the store starts empty (P08 convention: startup survives
// corrupt persisted state) instead of crashing.
if (!await FactStoreStartup.TryLoadAsync(factStore, factsPath))
{
    Console.WriteLine($"(facts file {factsPath} unreadable — starting with an empty store)");
}
var userMemory = new UserMemoryProvider(
    factStore,
    userId: "demo-user",
    extractor: new ChatClientFactExtractor(OllamaChat.Create()));

var agent = new ChatClientAgent(OllamaChat.Create(), new ChatClientAgentOptions
{
    Name = "MemoryBot",
    Description = "A helpful assistant that remembers prior conversations.",
    ChatOptions = new ChatOptions
    {
        Instructions = "Answer briefly. Use remembered context from earlier conversations when it is relevant.",
    },
    AIContextProviders = [memory, userMemory],
});

// Modes:
//   (no args)      scripted two-session demo in one process: tell in session A,
//                  then ask the recall question in a brand-new session B.
//   tell [text]    run only the "tell" phase, then exit (second process run).
//                  With no text, states the demo preference.
//   recall [q]     run only the "recall" phase (fresh process, empty in-memory
//                  state — only the facts file carries memory across).
//   repl           interactive chat; `mem list` / `mem clear` inspect and reset
//                  the durable fact store, everything else goes to the agent.
const string DefaultTell = "Remember: I prefer email over phone.";
const string DefaultRecall = "How should we contact you about my ticket?";
var mode = args.FirstOrDefault();
var text = string.Join(' ', args.Skip(1)).Trim();
switch (mode)
{
    case null:
        await ScriptedDemoAsync(agent, factStore, factsPath);
        break;
    case "tell":
        await TellAsync(agent, factStore, factsPath, text.Length > 0 ? text : DefaultTell);
        break;
    case "recall":
        await RecallAsync(agent, factStore, factsPath, text.Length > 0 ? text : DefaultRecall);
        break;
    case "repl":
        await ReplAsync(agent, factStore, factsPath);
        break;
    default:
        Console.WriteLine("usage: dotnet run [-- tell [text] | recall [question] | repl]  (no args = scripted demo)");
        return;
}

// Session 1 states a preference; session 2 asks a question that only makes
// sense if the memory provider injected session 1's messages as context.
static async Task ScriptedDemoAsync(ChatClientAgent agent, FactMemoryStore factStore, string factsPath)
{
    AgentSession sessionA = await agent.CreateSessionAsync();
    Console.WriteLine("== session A (new) ==");
    Console.WriteLine("user> Remember: I prefer email over phone.");
    var tell = await agent.RunAsync("Remember: I prefer email over phone.", sessionA);
    Console.WriteLine($"bot> {tell.Text}");
    await factStore.SaveAsync(factsPath); // fact memory persists across processes

    // A different session id — same process, but a fresh conversation with
    // no shared chat history; only the vector-backed memory provider can
    // carry the preference across.
    AgentSession sessionB = await agent.CreateSessionAsync();
    Console.WriteLine("== session B (new) ==");
    Console.WriteLine("user> How should we contact you about my ticket?");
    var recall = await agent.RunAsync("How should we contact you about my ticket?", sessionB);
    Console.WriteLine($"bot> {recall.Text}");
}

static async Task TellAsync(ChatClientAgent agent, FactMemoryStore factStore, string factsPath, string message)
{
    AgentSession session = await agent.CreateSessionAsync();
    Console.WriteLine("== session A (new) ==");
    Console.WriteLine($"user> {message}");
    var tell = await agent.RunAsync(message, session);
    Console.WriteLine($"bot> {tell.Text}");
    // Unlike the in-process chat-history store, the fact store persists:
    // `dotnet run -- recall` in a fresh process will still find the fact.
    await factStore.SaveAsync(factsPath);
    Console.WriteLine($"(facts saved to {factsPath}; chat-history store contents are gone)");
}

static async Task RecallAsync(ChatClientAgent agent, FactMemoryStore factStore, string factsPath, string message)
{
    AgentSession session = await agent.CreateSessionAsync();
    Console.WriteLine("== session B (new) ==");
    Console.WriteLine($"user> {message}");
    var recall = await agent.RunAsync(message, session);
    Console.WriteLine($"bot> {recall.Text}");
    // The provider extracts and stores facts during EVERY run — recall
    // included. Persist them like every other mode (tell/repl/scripted
    // already save), or facts learned in recall mode die with the process.
    await factStore.SaveAsync(factsPath);
}

// Interactive chat over one session, plus two window into the durable fact
// store: `mem list` enumerates what the extractor remembered so far (direct
// collection read — no model, no embeddings), `mem clear` resets it (and
// rewrites the facts file so the clear survives a restart). Every turn saves
// the fact store, so a REPL conversation carries into a later process.
static async Task ReplAsync(ChatClientAgent agent, FactMemoryStore factStore, string factsPath)
{
    const string userId = "demo-user"; // same pinned scope as the providers
    AgentSession session = await agent.CreateSessionAsync();
    Console.WriteLine("== P14 repl — chat naturally; commands: mem list, mem clear, quit ==");
    while (true)
    {
        Console.Write("user> ");
        var line = Console.ReadLine();
        if (line is null)
        {
            break; // stdin closed (piped/EOF input) — exit cleanly
        }

        line = line.Trim();
        if (line.Length == 0)
        {
            continue;
        }

        if (line is "quit" or "exit")
        {
            break;
        }

        if (line == "mem list")
        {
            var facts = await factStore.ListAsync(userId);
            if (facts.Count == 0)
            {
                Console.WriteLine("(no facts stored)");
            }

            foreach (var fact in facts)
            {
                Console.WriteLine($"  [{fact.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}] {fact.Text}");
            }

            continue;
        }

        if (line == "mem clear")
        {
            var removed = await factStore.ClearAsync(userId);
            await factStore.SaveAsync(factsPath);
            Console.WriteLine($"(cleared {removed} fact(s); {factsPath} updated)");
            continue;
        }

        var reply = await agent.RunAsync(line, session);
        Console.WriteLine($"bot> {reply.Text}");
        await factStore.SaveAsync(factsPath);
    }
}
