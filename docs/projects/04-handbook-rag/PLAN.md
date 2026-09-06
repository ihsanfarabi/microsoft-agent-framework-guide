# P04: HandbookRag — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agent grounded in the IT handbook: local embeddings + cosine top-k retrieval injected via a custom `AIContextProvider`.

**Architecture:** Corpus markdown → chunker → embedded index (built at startup, cached to `index.json`). `HandbookRetriever` takes query + embedder interface (`IEmbedder`, fake in tests, Ollama in prod). `HandbookContextProvider` overrides `ProvideAIContextAsync`, embeds the latest user message, injects top-3 chunks as a context message. Agent created with `ChatClientAgentOptions { AIContextProviders = [...] }` — verified API.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` (prerelease), `OllamaSharp` (embedding client), xUnit.

**Spec:** `docs/projects/04-handbook-rag/SPEC.md`

## Global Constraints

- Chat model: `glm-5.3-flash:cloud`. Embedding model: `nomic-embed-text` (local; run `ollama pull nomic-embed-text` first).
- Context provider API (verified, https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/context-providers): subclass `Microsoft.Agents.AI.AIContextProvider`, override `ProvideAIContextAsync(InvokingContext, CancellationToken)` returning `AIContext { Messages }`; attach via `ChatClientAgentOptions.AIContextProviders`; session-specific state goes in `AgentSession` (via `ProviderSessionState<T>`), never provider fields.
- Embedder is an interface so retrieval is testable without Ollama.
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: Corpus + chunker (TDD)

**Files:**
- Create: `docs/corpus/vpn-policy.md`, `password-reset.md`, `rma-hardware.md`, `software-install-policy.md`, `wifi-setup.md`, `email-setup.md`, `security-incident.md`, `onboarding.md`, `license-management.md`, `backup-policy.md`
- Create: `src/MafDemo.Core/Handbook/HandbookChunker.cs`, `HandbookChunk.cs`
- Test: `tests/MafDemo.Core.Tests/HandbookChunkerTests.cs`

**Interfaces:**
- Produces: `record HandbookChunk(string Doc, int Index, string Text)`; `static class HandbookChunker { IReadOnlyList<HandbookChunk> Chunk(string doc, string text, int maxChars = 500); }`

- [x] **Step 1: Write corpus docs** — each doc 3–6 short paragraphs with concrete checkable facts, e.g. `onboarding.md` must contain: "Employees get 25 vacation days per year." and "Laptops are refreshed every 4 years."; `vpn-policy.md`: "VPN reconnects must use MFA every 8 hours."; `rma-hardware.md`: "RMA requests must be filed within 14 days of failure." Keep every doc under ~1500 chars.

- [x] **Step 2: Write failing tests**

```csharp
// tests/MafDemo.Core.Tests/HandbookChunkerTests.cs
using MafDemo.Core.Handbook;

public class HandbookChunkerTests
{
    [Fact]
    public void Empty_doc_yields_no_chunks()
        => Assert.Empty(HandbookChunker.Chunk("empty.md", ""));

    [Fact]
    public void Oversized_doc_splits_at_max_chars()
    {
        var text = string.Join("\n", Enumerable.Repeat("Sentence about VPN policy. VPN is mandatory.", 40)); // ~2000 chars
        var chunks = HandbookChunker.Chunk("vpn-policy.md", text, maxChars: 500);
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 500));
    }

    [Fact]
    public void Chunks_carry_doc_name_and_sequence()
    {
        var chunks = HandbookChunker.Chunk("onboarding.md", "First para.\n\nSecond para.");
        Assert.Equal("onboarding.md", chunks[0].Doc);
        Assert.Equal(0, chunks[0].Index);
    }
}
```

- [x] **Step 3: Run, verify FAIL** — `dotnet test tests/MafDemo.Core.Tests`

- [x] **Step 4: Implement** — split on blank lines, greedily pack paragraphs up to `maxChars`, hard-split any paragraph exceeding `maxChars`:

```csharp
// src/MafDemo.Core/Handbook/HandbookChunker.cs
namespace MafDemo.Core.Handbook;
public record HandbookChunk(string Doc, int Index, string Text);

public static class HandbookChunker
{
    public static IReadOnlyList<HandbookChunk> Chunk(string doc, string text, int maxChars = 500)
    {
        var chunks = new List<HandbookChunk>();
        var current = "";
        int index = 0;
        void Flush() { if (current.Trim().Length > 0) chunks.Add(new(doc, index++, current.Trim())); current = ""; }

        foreach (var para in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var p = para.Trim();
            if (p.Length > maxChars)
            {
                Flush();
                for (int i = 0; i < p.Length; i += maxChars)
                    chunks.Add(new(doc, index++, p.Substring(i, Math.Min(maxChars, p.Length - i))));
                continue;
            }
            if (current.Length + p.Length + 2 > maxChars) Flush();
            current = current.Length == 0 ? p : current + "\n\n" + p;
        }
        Flush();
        return chunks;
    }
}
```

- [x] **Step 5: Run, verify PASS. Commit** — `feat(core): handbook corpus + chunker`

### Task 2: Retriever (TDD with fake embedder)

**Files:**
- Create: `src/MafDemo.Core/Handbook/IEmbedder.cs`, `HandbookRetriever.cs`
- Test: `tests/MafDemo.Core.Tests/HandbookRetrieverTests.cs`

**Interfaces:**
- Produces: `interface IEmbedder { Task<float[]> EmbedAsync(string text); }`
- Produces: `class HandbookRetriever(IEmbedder embedder)` — `Task BuildAsync(IReadOnlyList<HandbookChunk> chunks)`; `Task<IReadOnlyList<HandbookChunk>> SearchAsync(string query, int topK = 3)` (cosine similarity)

- [x] **Step 1: Write failing tests** — fake embedder maps keywords to fixed vectors:

```csharp
// tests/MafDemo.Core.Tests/HandbookRetrieverTests.cs
using MafDemo.Core.Handbook;

public class KeywordEmbedder : IEmbedder   // deterministic: word order vector
{
    public Task<float[]> EmbedAsync(string text)
    {
        var v = new float[64];
        foreach (var word in text.ToLower().Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ch in word) v[ch % 64] += 1f;
        return Task.FromResult(v);
    }
}

public class HandbookRetrieverTests
{
    private static readonly HandbookChunk[] Chunks =
    [
        new("onboarding.md", 0, "Employees get 25 vacation days per year."),
        new("vpn-policy.md", 0, "VPN reconnects must use MFA every 8 hours."),
        new("backup-policy.md", 0, "Backups run nightly at 2am to the Franklin region."),
    ];

    [Fact]
    public async Task Search_returns_relevant_chunk_first()
    {
        var r = new HandbookRetriever(new KeywordEmbedder());
        await r.BuildAsync(Chunks);
        var hits = await r.SearchAsync("how many vacation days do I get?");
        Assert.Equal("onboarding.md", hits[0].Doc);
    }

    [Fact]
    public async Task Search_respects_topK()
    {
        var r = new HandbookRetriever(new KeywordEmbedder());
        await r.BuildAsync(Chunks);
        Assert.Equal(2, (await r.SearchAsync("backups", topK: 2)).Count);
    }
}
```

- [x] **Step 2: Run, verify FAIL.**

- [x] **Step 3: Implement** — `BuildAsync` embeds every chunk (parallel), stores (vector, chunk); `SearchAsync` embeds query, ranks by cosine, returns top K. Cosine helper: dot/(‖a‖·‖b‖); guard zero norm → 0.

- [x] **Step 4: Run, verify PASS. Commit** — `feat(core): handbook retriever with cosine ranking`

### Task 3: Ollama embedder + context provider

**Files:**
- Create: `src/P04.HandbookRag/` console project, `OllamaEmbedder.cs`, `HandbookContextProvider.cs`, `HandbookBot.cs`, `Program.cs`
- Modify: `MafDemo.sln`

**Interfaces:**
- Consumes: `IEmbedder`, `HandbookRetriever` (Task 2), `OllamaChat` pattern (P01)
- Produces: `class OllamaEmbedder : IEmbedder` (model `nomic-embed-text`); `class HandbookContextProvider : AIContextProvider`

- [x] **Step 1: Scaffold project** — console `P04.HandbookRag`, add to sln, packages `Microsoft.Agents.AI --prerelease`, `OllamaSharp`, reference Core. `ollama pull nomic-embed-text`.

- [x] **Step 2: `OllamaEmbedder`** — via OllamaSharp `OllamaApiClient.EmbedAsync` (verify method on https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/chat-local-model + OllamaSharp docs):

```csharp
using MafDemo.Core.Handbook;
using OllamaSharp;

public class OllamaEmbedder : IEmbedder
{
    private readonly OllamaApiClient _client;
    public OllamaEmbedder(string? model = null)
        => _client = new(new Uri(Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434"),
            model ?? "nomic-embed-text");

    public async Task<float[]> EmbedAsync(string text)
        => (await _client.EmbedAsync(text, model: "nomic-embed-text")) // verify exact call from OllamaSharp docs
            .ToArray();
}
```

- [x] **Step 3: `HandbookContextProvider`** — verified pattern from context-providers doc:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MafDemo.Core.Handbook;

public class HandbookContextProvider(HandbookRetriever retriever) : AIContextProvider(null, null)
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var latestUser = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User);
        if (latestUser is null) return new AIContext();

        var hits = await retriever.SearchAsync(latestUser.Text, topK: 3);
        if (hits.Count == 0) return new AIContext();

        var handbook = string.Join("\n---\n", hits.Select(h => $"[{h.Doc} #{h.Index}]\n{h.Text}"));
        return new AIContext
        {
            Messages = [new ChatMessage(ChatRole.User,
                "Company IT handbook excerpts (cite the [doc] you use):\n" + handbook)]
        };
    }
}
```

- [x] **Step 4: `HandbookBot` + `Program.cs`** — build index from `docs/corpus/*.md` at startup (chunk each file, `BuildAsync`), then:

```csharp
var chatClient = OllamaChat.Create();                    // P01 helper (copy into P04 or promote to Core)
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    ChatOptions = new() { Instructions =
        "You are HelpDeskHQ's handbook bot. Answer ONLY from the provided handbook excerpts. " +
        "Cite the doc filename in square brackets. If the excerpts do not answer the question, say exactly: 'That is not in the handbook.'" },
    AIContextProviders = [new HandbookContextProvider(retriever)],
});
Console.WriteLine(await agent.RunAsync("How many vacation days do I get?"));
```
(`ChatClientAgent` constructor overloads: if `(IChatClient, ChatClientAgentOptions)` doesn't exist, use the name/instructions overload + options — copy exact shape from context-providers doc sample.)

- [x] **Step 5: Run**

Run: `dotnet run --project src/P04.HandbookRag`
Expected: "25 vacation days" + `[onboarding.md]` cited.

- [x] **Step 6: Commit** — `feat(p04): grounded handbook agent`

### Task 4: Guardrail verification

**Files:**
- Modify: `src/P04.HandbookRag/Program.cs` (scenario list)
- Create: `docs/projects/04-handbook-rag/NOTES.md`

- [x] **Step 1: Scripted scenarios** — corpus question (vacation days), corpus question from a different doc ("When must an RMA be filed?"), guardrail question ("What is the CEO's home address?"). Expected: two grounded + cited, third → "That is not in the handbook."

- [x] **Step 2: If guardrail fails** (model invents an answer): strengthen instructions, add "before answering, check the excerpts contain the fact verbatim" — record what worked in NOTES.md.

- [x] **Step 3: NOTES.md** — bullets: auto-injection vs tool retrieval (see stretch), what the trace shows before model call, guardrail behavior.

- [x] **Step 4: Commit** — `docs(p04): rag notes + guardrail verification`

### Task 5: Stretch

- [x] **Step 1:** `search_handbook` tool variant (P02 `AIFunctionFactory` pattern); run same questions; record when model used tool vs relied on injected context.
- [x] **Step 2: Commit** — `feat(p04): tool-based retrieval variant`