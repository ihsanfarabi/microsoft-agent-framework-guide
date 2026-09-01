# SPEC — P14: SemanticMemory (agent memory across sessions)

## Story

"Remember: I prefer email over phone." Restart. "How should I reach you?" —
the agent answers from recalled memory. Two MAF memory surfaces, contrasted:

1. **First-class baseline** — `Microsoft.Agents.AI.ChatHistoryMemoryProvider`:
   after each invoke embeds and stores raw chat turns into a
   `Microsoft.Extensions.VectorData` store; before each invoke retrieves
   similar past messages across sessions. Scope via `storageScope`/`searchScope`
   (UserId/SessionId).
2. **Extracted-fact memory** (the "prefers email" flavor): custom
   `AIContextProvider` (`UserMemoryProvider`) — on each turn, search a fact
   store for the latest external user message, inject "Here are some
   memories…"; after the turn, a small extractor agent distills durable facts
   as JSON string[], deduped by cosine ≥ 0.9.

## Success criteria

- Session 1: agent told a preference; new process run (same fact store) recalls it unprompted.
- `ChatHistoryMemoryProvider` baseline demoed separately from the fact store.
- Fact store TDD'd in `MafDemo.Core.Tests`: add/recall/dedupe/persist survive restart.
- Unrelated queries inject nothing (topK small, threshold — assert no-memory case in test).
- NOTES contrasts: turns vs facts vs P04 fixed-corpus RAG.

## Verified facts (P14 research)

- `ChatHistoryMemoryProvider` (+ `ChatHistoryMemoryProviderOptions/Scope`, `SearchBehavior { BeforeAIInvoke, OnDemandFunctionCalling }`, `.State(...)`, `Search(...)` tool) ships in `Microsoft.Agents.AI` 1.19.0 (verified in installed DLL strings). Wire: `ChatClientAgentOptions.AIContextProviders`.
- `Microsoft.Extensions.VectorData.Abstractions` 10.7.0 comes transitively with MAF 1.19.0 — no extra package.
- `OllamaApiClient` natively implements `IEmbeddingGenerator<string, Embedding<float>>` (OllamaSharp 5.4.30) → plugs straight into `InMemoryVectorStoreOptions.EmbeddingGenerator`. bge-m3 = 1024 dims — one constant.
- Vector store package: `Microsoft.SemanticKernel.Connectors.InMemory` 1.74.x-preview (what official docs use; no `Microsoft.Extensions.VectorData.InMemory` package exists). Record: `[VectorStoreRecord]` + `VectorStoreRecordKeyProperty` / `VectorDataProperty`.
- Extraction filter: use `AgentRequestMessageSourceType.External` to avoid feedback loops; session state via `ProviderSessionState<T>`.
- Persistence: `InMemoryVectorStore` is process-local → persist `MemoryFact`s as JSON, re-embed on startup (bge-m3 local, cheap). SQLite-vec = stretch only.

## Risks

Extractor quality on glm-5.3-flash (over/under-extraction, inconsistent
phrasing) → JSON-only prompt, low temperature, dedupe threshold, expect prompt
tuning = eval material. Negation ("prefers email" vs "hates email") not caught
by similarity dedupe — keep topK ≤ 3, store timestamp, instruction to treat memories as possibly stale.

## Resources

- https://learn.microsoft.com/en-us/agent-framework/integrations/chat-history-memory-provider
- https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/context-providers
- https://learn.microsoft.com/en-us/agent-framework/get-started/memory
- API: https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.chathistorymemoryprovider?view=agent-framework-dotnet-latest