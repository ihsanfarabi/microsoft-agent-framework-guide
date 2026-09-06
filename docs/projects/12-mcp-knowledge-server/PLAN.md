# P12 McpKnowledgeServer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A hand-written C# stdio MCP server (`search_knowledge` over `docs/corpus`) consumed by P02's TicketBot as a second tool source.

**Architecture:** New console project `P12.McpKnowledgeServer` (ModelContextProtocol 2.2.0 + Microsoft.Extensions.Hosting) whose only tool scores handbook chunks by token overlap — embedding-free, reusing `MafDemo.Core`'s `HandbookChunker`. P02's existing MCP-client block gains a second stdio client pointing at this server via `dotnet run`.

**Tech Stack:** ModelContextProtocol 2.2.0, Microsoft.Extensions.Hosting, MafDemo.Core (HandbookChunker), xUnit.

**Spec:** `docs/projects/12-mcp-knowledge-server/SPEC.md`

## Global Constraints

- Protocol stream integrity rule applies project-wide: **no `Console.WriteLine` / Console.Out writes in the server** — logging goes to stderr only.
- Same as P11 global constraints: .NET 10, live tests gated `RUN_EVALS=1`, conventional commits `type(p12): ...` + Co-Authored-By trailer, RTK git.

---

### Task 1: KnowledgeScorer — TDD in MafDemo.Core

**Files:**
- Create: `src/MafDemo.Core/Handbook/KnowledgeScorer.cs`
- Test: `tests/MafDemo.Core.Tests/KnowledgeScorerTests.cs`

**Interfaces:**
- Consumes: `MafDemo.Core.Handbook.HandbookChunker` (chunk shape: doc name + text).
- Produces: `static string Search(string query, int maxResults, IReadOnlyList<(string Doc, string Text)> chunks)` returning `doc | score | excerpt` lines; `static double Score(string query, string text)` token-overlap scorer.

- [x] **Step 1: failing tests**

```csharp
[Fact]
public void Score_prefers_token_overlap()
{
    double hit = KnowledgeScorer.Score("password expired", "VPN password expires every 90 days.");
    double miss = Score("password expired", "Printer queue stuck in spooler loop.");
    Assert.True(hit > 0 && hit > miss);
}

[Fact]
public void Search_ranks_known_doc_first()
{
    var chunks = HandbookChunker.Chunk("password-reset", File.ReadAllText(corpusDir.FullName + "/password-reset.md")); // loader hand-rolled in-task; the shared API that later shipped is HandbookCorpus.Locate()
    var top = KnowledgeScorer.Search("password expired", 1, chunks);
    Assert.Contains("password", top, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 2:** run `rtk dotnet test tests/MafDemo.Core.Tests` → FAIL (missing type).
- [x] **Step 3: implement** — `Score`: lowercase tokens (`char.IsLetterOrDigit` split), intersect query tokens with chunk tokens / sqrt(|queryTokens|) (TF-free BM25-lite). `Search`: score all chunks, order, take `maxResults`, format lines.
- [x] **Step 4:** tests green. Commit `feat(core): token-overlap knowledge scorer (P12)`.

### Task 2: The stdio MCP server

**Files:** Create `src/P12.McpKnowledgeServer/P12.McpKnowledgeServer.csproj`, `src/P12.McpKnowledgeServer/Program.cs`, `src/P12.McpKnowledgeServer/KnowledgeTools.cs`.

**Interfaces:**
- Consumes: `KnowledgeScorer.Search(query, maxResults, chunks)` from Task 1; chunk loading via `HandbookChunker` + `File.ReadAllText` over `args[0]` (default: BaseDirectory-relative walk-up to `docs/corpus`, now shared as `MafDemo.Core`'s `HandbookCorpus.Locate(overridePath)`).
- Produces: MCP tool `SearchKnowledge(query, maxResults = 3)` over stdio.

- [x] **Step 1: scaffold + wire** — csproj: net10.0, `ModelContextProtocol 2.2.0`, `Microsoft.Extensions.Hosting`, ProjectReference `MafDemo.Core`. Server host:

```csharp
var builder = Host.CreateApplicationBuilder(args);
// stdout is the JSON-RPC protocol stream — everything else logs to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

[McpServerToolType]
public static partial class KnowledgeTools
{
    [McpServerTool, Description("Search the MafCorp IT handbook. Returns doc | score | excerpt lines.")]
    public static string SearchKnowledge(string query, int maxResults = 3)
        => KnowledgeScorer.Search(query, maxResults, ChunkCache.Chunks);
}
```
(`ChunkCache` = small static lazy loader in `KnowledgeTools.cs` reading corpus dir from `args[0]`/walk-up.)
- [x] **Step 2: smoke via dotnet run** — server must start and idle (no output on stdout). Verify with a 5 s timeout run: exit only via timeout.
- [x] **Step 3:** build green, commit `feat(p12): stdio MCP knowledge server`.

### Task 3: Consume from P02 alongside filesystem tools

**Files:** Modify `src/P02.TicketTools/Program.cs`.

- [x] **Step 1: second client + merge** — after the existing filesystem-MCP block, spawn the knowledge server (prebuilt binary path if `dotnet run` child proves flaky under timeout — prefer `dotnet run --project` first), `ListToolsAsync()`, pass both tool sets into one `CreateWithMcp` agent, scripted question `"What's the policy if my password expires?"`, print the answer + tool trace.
- [x] **Step 2: verify** — answer references corpus content (password/VPN doc); OTel span shows the MCP tool call.
- [x] **Step 3: commit** `feat(p02): consume P12 knowledge server via stdio MCP`.
- [x] **Step 4: guard the protocol stream** — add one test asserting `KnowledgeScorer.Search` never throws on empty/whitespace query (server crash = stream death). Run full test project. Commit included in step 3 if small.

### Task 4: Docs + portfolio

- [x] **Step 1:** `docs/projects/12-mcp-knowledge-server/NOTES.md` — SDK API truth (survived 0.x→2.2.0), stdout-poisoning pitfall if hit, corpus-path resolution story.
- [x] **Step 2:** README ladder + PORTFOLIO table row. Full suite green. Commit `docs(p12): notes + portfolio entries`.