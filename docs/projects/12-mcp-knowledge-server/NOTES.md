# P12 — McpKnowledgeServer notes

A custom MCP *server* in C#: `P12.McpKnowledgeServer` exposes `search_knowledge`
over the MafCorp handbook (token-overlap scoring, no embeddings) via the
official `ModelContextProtocol` SDK 2.2.0, and `P02.TicketTools` consumes it as
a second stdio client alongside its filesystem server.

## What worked

- **The stdio-server API survived 0.x→2.2.0 unchanged.** The churn between the
  C# SDK's 0.x line and 2.x landed in Tasks (SEP-2663), stateless HTTP
  transport, and sampling/elicitation — the server path this project uses
  (`builder.Services.AddMcpServer().WithStdioServerTransport()
  .WithToolsFromAssembly()` + `[McpServerToolType]` / `[McpServerTool]`) is
  byte-for-byte the shape the older docs show. The v2.0 breaking-changes post
  reads scarier than the diff actually is for plain tool servers.
- **`dotnet run` works as a child MCP transport.** The P02 client launches the
  server with `Command = "dotnet", Arguments = ["run", "--project", …]`. Probed
  the raw JSON-RPC stream before committing: `initialize` round-trips return a
  clean single stdout line both with a prebuilt project and after a forced
  incremental rebuild (msbuild chatter goes to stderr), so no
  `dotnet build -o <tmp>` prebuilt-binary fallback was ever needed.
- **Corpus resolution from a child working dir**: `ChunkCache` accepts
  `args[0]` if it's an existing directory, else walks up from
  `AppContext.BaseDirectory` to `docs/corpus` (same pattern as P10's
  `FindCorpusDirectory`). The child inherits whatever cwd the client spawns it
  with, so the walk-up is what makes the launch robust from any `bin/` dir.
- **Lazy one-shot loading** (`LazyThreadSafetyMode.ExecutionAndPublication`
  static `ChunkCache`) — the corpus is read once, then every tool call is pure
  scoring. `HandbookChunker` from Core is reused as-is.

## Doc-vs-reality divergences

- **MAF's own docs ship a stale client snippet.** The local-mcp-tools page
  still shows `McpClientFactory.CreateAsync(...)` — that is the 0.x SDK API and
  does not exist in 2.x. The real 2.x shape is
  `McpClient.CreateAsync(new StdioClientTransport(new() { Name, Command,
  Arguments }))`. P02 had already learned this on its own; the docs have not
  caught up.
- **The SDK snake_cases tool names on the wire.** The C# method is
  `SearchKnowledge` (kept PascalCase per plan), but `tools/list` and the model
  both see **`search_knowledge`** — ModelContextProtocol 2.x converts method
  names to snake_case by default. Everything downstream (the P02 merged-tool
  guard, the OTel `gen_ai.tool.name` span attribute) must use the wire name.
  `[McpServerTool(Name = ...)]` would override it if a different name were
  wanted.
- **stdout poisoning is the whole game for stdio servers.** Rule enforced in
  P12: zero `Console` usage anywhere; all host logging routed with
  `builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold =
  LogLevel.Trace)` so everything lands on stderr. Verified, not assumed —
  **0 bytes on stdout** over both a 6 s idle run and a live JSON-RPC session
  (initialize + tools/list + tools/call), while "Hosting environment:
  Production" and shutdown messages streamed on stderr.
- **The brief's `HandbookCorpus.LoadAll` doesn't exist.** The plan said "same
  loader P04 uses" — no such loader exists in Core or P04 (P04 loads the corpus
  inline in `Program.cs`). Task 1 wrote a test-local loader; Task 2 promoted
  that shape into the server's `ChunkCache`. A shared loader in Core is still
  owed (see "differently next time").
- **The scorer's tie-break exists because of the real corpus.** On the
  textbook example the spec formula ties three ways: for query "password
  expired", `password-reset.md`, `onboarding.md`, and `wifi-setup.md` each
  match exactly one distinct query token. `OrderByDescending(Score)` alone made
  ranking nondeterministic; the shipped tie-break (raw query-token frequency,
  then input index) puts `password-reset.md` first — what a user means. The
  corpus broke the unit-test design, not the other way round.

## What to do differently next time

- Promote the handbook loader into `MafDemo.Core` (`HandbookCorpus.LoadAll` was
  the right *idea*, wrong assumption about it existing). Three consumers now
  hand-roll the same walk-up + chunk loop: P04 inline, the Task 1 test, and
  `ChunkCache`.
- Budget for the tie-break when writing a "simple formula" scorer test against
  real content, not synthetic chunks — the deterministic-ordering rule
  (frequency, then index) was an unplanned but necessary spec extension.
- When consuming your own MCP server, write the merged-tool guard before the
  demo run: P02 throws if the merged tool set lacks `search_knowledge`, so a
  silently-degraded child launch fails loudly instead of producing an
  ungrounded answer.
- Score formatting uses the process culture (`{score:F2}` rendered `0,71`
  under a comma-decimal locale) — cosmetic, but a culture-invariant format is
  the right call for a machine-read wire response.
- SDK churn is now two-sided: MAF's MCP doc pages lag the C# SDK (client
  factory API), while the SDK's own server surface is stable. Check the SDK
  repo, not MAF docs, for anything transport- or protocol-shaped.
