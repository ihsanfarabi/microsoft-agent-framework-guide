# P14 — SemanticMemory notes

Two memory strategies running side by side in one `ChatClientAgent`: MAF's
`ChatHistoryMemoryProvider` (T1 baseline — verbatim chat turns into a vector
store, auto-injected before each run) and a custom `UserMemoryProvider`
(T3) that distills each turn into durable third-person facts ("User prefers
email over phone") in a `FactMemoryStore` (T2) which persists to JSON. The
demo payoff: `dotnet run -- tell` in one process, `dotnet run -- recall` in a
fresh one still answers from memory (`scripts/demo14.sh`).

## Turns vs facts vs fixed-corpus RAG — when each shape fits

| | Turns (T1 `ChatHistoryMemoryProvider`) | Facts (T2/T3 custom provider) | Fixed-corpus RAG (P04/P12) |
|---|---|---|---|
| What is stored | Verbatim `ChatMessage` records (role, content, ids) embedded whole | Short distilled third-person sentences, one vector each | Static document chunks, embedded once at index time |
| Who writes | The framework (`InvokedAsync` after every run) | A second model call (the extractor) after each run | Nobody at runtime — the corpus is fixed |
| Lifetime | Process-local (InMemory store) | Durable (JSON file, reloaded at startup) | Durable, shared by all users |
| Scope | Per-user via `ChatHistoryMemoryProviderScope` (SessionId omitted → crosses sessions) | Per-user (`UserId` filter on every recall) | Per-corpus, user-independent |
| Fits | "What did we talk about?" — continuity of conversation | "What do you know about ME?" — preferences, commitments | "What does the handbook say?" — grounding in shared knowledge |
| Cost per turn | One embedding per stored message | One extra chat call (extraction) + embeddings | Zero at runtime (embed only the query) |

The contrast is the curriculum point: a chat-history store answers "what did
we say", a fact store answers "who are you", and RAG answers "what does the
manual say". None of them substitutes for another; P14 wires turns + facts
side by side (`AIContextProviders = [memory, userMemory]`) so both inject
before each run.

## The real 1.19.0 `ChatHistoryMemoryProvider` API (as implemented)

The plan/research snippet drifted from the shipped API in three ways, all
verified against the installed assemblies and XML docs
(`~/.nuget/packages/microsoft.agents.ai/1.19.0/...`) plus the official
`AgentWithMemory_Step01_ChatHistoryMemory` sample:

- **Single constructor, no fluent `.State(...)`.** Real:
  `ChatHistoryMemoryProvider(VectorStore vectorStore, string collectionName,
  int vectorDimensions, Func<AgentSession?, State> stateInitializer,
  ChatHistoryMemoryProviderOptions? options = null, ILoggerFactory?
  loggerFactory = null)`. Scopes go into the `stateInitializer` delegate; the
  vector dimensions are a ctor parameter, not a `State` argument. (A
  `ChatHistoryMemoryProvider.State(storageScope, searchScope)` nested type
  exists — its `StorageScope`/`SearchScope` are get-only positional ctor
  params — but there is no fluent chain.)
- **No `Search` property, no `AgentScope` type.** A
  `ChatHistoryMemoryProviderScope` has exactly four nullable string fields:
  `UserId`, `AgentId`, `SessionId`, `ApplicationId`. Scoping is by *value*:
  a field left null spans everything. Cross-session recall is therefore
  expressed by **omitting** `SessionId` from both scopes — the snippet's
  `Search = new[] { AgentScope.UserId }` selects nothing because the type
  does not exist.
- **`AIContextProviders` on `ChatClientAgentOptions` was correct** — the one
  part of the snippet that matched. The provider is invoked at each
  `RunAsync` (`InvokingAsync` injects, end-of-run `InvokedAsync` stores).
- **Silent failure mode worth remembering:** the provider swallows search and
  storage exceptions into `ILogger` errors. Passing a real `loggerFactory`
  to the *provider* (not just the agent) is what makes failures visible —
  this cost most of T1's debugging time. The T3 `UserMemoryProvider` copies
  the lesson (`NullLoggerFactory` is only the offline default).

## InMemoryVectorStore: SK Connectors → CommunityToolkit swap

The plan named no package; `Microsoft.SemanticKernel.Connectors.InMemory`
1.74.0-preview is the obvious candidate and is a **trap**: it is built
against vectordata abstractions 10.1 and crashes at search time under the
10.7.0 that MAF 1.19.0 pins — `TypeLoadException: Could not load type
'Microsoft.Extensions.VectorData.VectorSearchFilter'` (`VectorSearchFilter`
was *removed* from the 10.7.0 abstractions; filters are lambda expressions
there). **`CommunityToolkit.VectorData.InMemory` 1.0.0** pins abstractions
10.7.0 exactly and is the store the official sample uses — swapped in, no
other change. The same abstractions gap explains why `FactMemoryStore`
(T2) passes a query *vector* to `SearchAsync` and a lambda to
`VectorSearchOptions<TRecord>.Filter` instead of the old filter type.

## The process-local limit: what the T1 baseline can and cannot do

`InMemoryVectorStore` lives and dies with the process. Concretely:

- **Works (T1):** cross-SESSION recall inside one process — a brand-new
  `AgentSession` finds messages stored under another session, because the
  scopes pin only `UserId` (omitted `SessionId` spans sessions).
- **Does not work (T1):** cross-PROCESS recall — `dotnet run -- tell`, quit,
  `dotnet run -- recall` in a fresh process finds an empty store, and the
  model honestly disclaims any memory. The provider's `GetDynamicCollection`
  records vanish with the heap; there is no persistence hook on the
  provider.
- **The durable path (T2/T3):** `FactMemoryStore` owns its own InMemory
  collection but persists it via the toolkit's
  `SerializeCollectionAsJsonAsync` / `DeserializeCollectionFromJsonAsync` to
  `p14-facts.json` (`LoadAsync` at startup, `SaveAsync` after each run). The
  demo script's two-process run exercises exactly this seam. Startup goes
  through `FactStoreStartup.TryLoadAsync`: missing file → empty store, corrupt
  file → warning and empty store (P08 convention — startup survives bad
  persisted state).

A production fix for the T1 baseline would be a durable `VectorStore` (SQLite/
Qdrant/...); the provider takes any `VectorStore`, so the wiring would not
change.

## Extraction prompt tuning

`ChatClientFactExtractor` is a tiny `ChatClientAgent` (same Ollama wiring,
same model) whose entire prompt is:

> You extract durable facts about the user from a conversation turn. Reply
> with ONLY a JSON array of short third-person sentences, e.g. ["User prefers
> email over phone", "User works night shifts"]. Include only stable
> preferences, facts about the user, or commitments — not small talk,
> questions, or transient remarks. If there is nothing worth remembering,
> reply with [].

The turn is handed over as a plain `"role: text"` transcript and the reply is
parsed by a tolerant `ParseFacts` (outermost `[...]` slice → `string[]`;
fences, prose wrappers, non-string items, truncation and blanks all yield an
empty list, never a throw).

Observed behavior and failure modes:

- **The happy path is reliable but verbose-tolerant.** Live runs returned
  exactly one clean fact for a one-sentence tell ("User prefers email over
  phone for anything urgent"). The parser exists because models wrap arrays
  in markdown fences or prose; the slice-based parse absorbed those in tests.
- **Nothing-to-remember is the silent case.** `[]` and any unparseable reply
  both mean "no facts" — the provider no-ops. That is the right default (a
  failed extraction must never break the agent run) but it makes
  *under-extraction* invisible: no log distinguishes "model said []" from
  "model answered prose we could not parse". Tuning means reading the raw
  extractor reply in a trace, not an error log.
- **Extraction costs a second chat call per turn** (visible in OTel: a second
  `chat glm-5.3-flash:cloud` span, ~160 input tokens for a short turn). For a
  demo this is fine; a production version would batch turns or extract on a
  schedule.
- **Third-person phrasing is load-bearing.** Facts are stored as "User
  prefers ..." so they stay true when re-injected as a system message in any
  future session; first-person storage would read as the *agent's* voice.
- **The extraction prompt does not stop the main model from hedging.** Both
  live tell runs had the agent reply "I don't retain information between
  separate conversations" — the model's base behavior — even though the fact
  was being extracted and persisted at that very moment. The recall run then
  contradicts it ("Based on what I have on file..."). Harmless for the demo,
  but a memory-enabled agent should probably get instructions that acknowledge
  its own memory.

## Dedupe gotchas

- **Dedupe is owned by the store, only by the store.** `FactMemoryStore.
  AddAsync` embeds the new text, fetches the user's existing facts, and if the
  best cosine neighbor is ≥ `DuplicateThreshold = 0.9` it **upserts in
  place** (same `Id`, new text/vector, fresh `CreatedAt`) instead of adding a
  second record. `UserMemoryProvider` calls `AddAsync` for every non-blank
  extracted fact and must not re-implement the check — two dedupe owners
  would drift (one compares, one doesn't) and the store's single
  `MaxFactsPerUser` scan bound would be bypassed.
- **Dedupe is user-scoped.** A near-duplicate under a different `UserId`
  never collapses — the neighbor scan filters `f.UserId == userId`. The demo
  user id is pinned (`"demo-user"`) at the provider, like the T1 scopes.
- **Recall does not dedupe.** Two stored near-paraphrases (both under the
  threshold) both surface; top-3 injection can waste slots on near-duplicates.
  Fix at write time, not read time.
- **0.9 cosine is a real-model judgment call.** The offline tests model
  near-duplicates with *identical* vectors (cosine 1.0). Real bge-m3
  paraphrases can score well below 0.9, so "I prefer email over phone" vs
  "Email is my preferred contact method" may store as two facts. That is the
  safe direction (no information loss), but store growth is the cost.
- **Upsert refreshes `CreatedAt`** — "when did the user last say this" is the
  only timestamp meaning available; there is no edit history.
- **Test-fake hazard:** a hash-fallback embedder that can collide with a
  table vector makes an unrelated string score cosine 1.0 and silently
  trigger an upsert. The test fake draws fallback one-hots only from indices
  no table vector occupies (locked by a regression test in
  `MafDemo.Core.Tests`).

## REPL / demo

- `dotnet run` (no args) keeps the T1 two-session scripted demo;
  `-- tell [text]` / `-- recall [question]` isolate the phases for the
  two-process demo; `-- repl` is interactive chat with `mem list` (direct
  collection read — no model call) and `mem clear` (deletes the user's facts
  and rewrites the file, so the clear survives a restart). Both need a store
  enumeration the T2 store did not expose: `ListAsync`/`ClearAsync` are a
  trivial passthrough added in T4, as pre-ruled in the SDD ledger.
- `scripts/demo14.sh` live-tested twice end to end (2026-09-01): process 1
  tell → fact `User prefers email over phone for anything urgent` persisted;
  fresh process 2 recall answered "Based on what I have on file, you prefer
  **email** over phone for urgent contact". The script checks Ollama and both
  models up front and fails loudly rather than hanging.
