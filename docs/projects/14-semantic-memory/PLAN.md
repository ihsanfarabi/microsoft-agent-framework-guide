# P14 SemanticMemory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ChatClientAgent` that remembers facts about a user across process
restarts — first via MAF's `ChatHistoryMemoryProvider`, then via a custom
fact-extracting `AIContextProvider` over the P14 vector store.

**Architecture:** New console project `P14.SemanticMemory` + `MafDemo.Core/Memory/`.
Embeddings: `OllamaApiClient` (bge-m3, 1024 dims) as `IEmbeddingGenerator` straight
into `InMemoryVectorStore`; facts persisted as JSON, re-embedded at startup.

**Tech Stack:** `Microsoft.Agents.AI` 1.19.0, `Microsoft.Extensions.VectorData.Abstractions`
(transitive), `Microsoft.SemanticKernel.Connectors.InMemory` 1.74.x, OllamaSharp.

**Spec:** `docs/projects/14-semantic-memory/SPEC.md`

## Global Constraints

- .NET 10; live tests gated `RUN_EVALS=1`; commits `type(p14): ...` + Co-Authored-By; RTK git; no edits to `src/P01`–`P10`.
- bge-m3 dimension constant `1024` defined once (`MemoryFacts.VectorDimensions`).

---

### Task 1: `ChatHistoryMemoryProvider` baseline

**Files:** Create `src/P14.SemanticMemory/` (csproj, `appsettings.json`, `Program.cs`) + slnx entry.

- [x] **Step 1: wire baseline**
```csharp
var vectorStore = new InMemoryVectorStore(new InMemoryVectorStoreOptions
    { EmbeddingGenerator = OllamaApiClient /* bge-m3 */ });
var memory = new ChatHistoryMemoryProvider(storage: vectorStore)
    .State(new ChatHistoryMemoryProviderScope
    {
        UserId = "demo-user",
        Search = new[] { AgentScope.UserId },
    },
    vectorDimensions: MemoryFacts.VectorDimensions);
// agent: ChatClientAgentOptions { AIContextProviders = [memory], ... }
```
(Exact option names from the installed XML — verify `ChatHistoryMemoryProvider` ctor/state signature before writing; the namespace is `Microsoft.Agents.AI`.)
- [x] **Step 2: scripted demo (live, manual)** — session 1: "Remember: I prefer email over phone." Exit. Rerun: "How should we contact you about my ticket?" Expect email mention. If schema of `storageScope`/`searchScope` differs, fix and record in NOTES.
- [x] **Step 3: commit** `feat(p14): ChatHistoryMemoryProvider cross-session baseline`.

### Task 2: FactMemoryStore — TDD in MafDemo.Core

**Files:** Create `src/MafDemo.Core/Memory/MemoryFact.cs`, `FactMemoryStore.cs`; Test `tests/MafDemo.Core.Tests/FactMemoryStoreTests.cs` (fake embedder: deterministic hash vectors — same string → same vector; distinct strings far apart — offline, no model).

**Interfaces:**
- Produces: `record MemoryFact(string Id, string UserId, string Text, DateTimeOffset CreatedAt, float[] Vector)` (or `[VectorStoreRecord]`-attributed record — attribute style per store type used, decide at implementation); `FactMemoryStore.AddAsync(string userId, string text)`, `Task<IReadOnlyList<MemoryFact>> RecallAsync(string userId, string query, int topK = 3)`, `LoadAsync()/SaveAsync(path)` file persistence.

- [x] **Step 1: failing tests** — add two distinct facts, recall with near-duplicate text returns the matching fact first; cosine ≥ 0.9 duplicate text upserts instead of adding; save → new store instance → `LoadAsync` recalls without re-adding.
- [x] **Step 2:** run → FAIL.
- [x] **Step 3: implement** with the fake embedder injected (store takes `IEmbeddingGenerator<string, Embedding<float>>` — OllamaApiClient implements it natively, P04's `OllamaEmbedding` wrapper remains for hand-rolled paths).
- [x] **Step 4:** green; commit `feat(core): dedupe-aware fact memory store (TDD)`.

### Task 3: `UserMemoryProvider : AIContextProvider`

**Files:** Create `src/P14.SemanticMemory/Memory/UserMemoryProvider.cs`; extractor agent; Modify `Program.cs`.

**Interfaces:**
- Consumes: `FactMemoryStore`; `OllamaChat.Create()` for a tiny extractor agent.
- Produces: `UserMemoryProvider(FactMemoryStore facts)` overriding `ProvideAIContextAsync(...)` (recall for latest external user message, inject summary message) + `StoreAIContextAsync(...)` (extract facts JSON string[] via extractor agent; skip empty; cosine ≥ 0.9 dedupe; filter `AgentRequestMessageSourceType.External`).

- [x] **Step 1:** implement provider per MAF context-providers doc (`ProvideAIContextAsync` / `StoreAIContextAsync` overrides; verify base signatures from installed XML).
- [x] **Step 2: live test gated `RUN_EVALS=1`** — tell preference, ask recall question in a *fresh* session, assert "email" appears in `Recall` output of the store (unit-level recall) and answer (hermetic part: fake extractor).
- [x] **Step 3:** commit `feat(p14): fact-extraction memory provider`.

### Task 4: CLI demo + contrast + docs

- [x] **Step 1:** `Program.cs` REPL: `mem list`, `mem clear`, natural chat; two-process demo script `scripts/demo14.sh` (run → quit → rerun → memory persists).
- [x] **Step 2:** `docs/projects/14-semantic-memory/NOTES.md` — turns vs facts vs fixed-corpus RAG; extractor prompt tuning notes; dedupe gotchas.
- [x] **Step 3:** README ladder + PORTFOLIO row; suite green; commit `docs(p14): notes + portfolio entries`.