# SPEC — P12: McpKnowledgeServer (write our own MCP server in C#)

**Tier:** Advanced · **Estimate:** 4–5 hours · **Depends on:** P02, P04

Closes the skipped P02 stretch goal: a custom C# MCP *server* exposing
`search_knowledge` over the MafCorp handbook, consumed by the P02 client.

## Story

P02 made HelpDeskHQ an MCP **client** (filesystem stdio server). This project
makes it a server author too: a stdio MCP server, `P12.McpKnowledgeServer`,
whose `search_knowledge` tool answers handbook questions with token-overlap
scoring (no embeddings — P04 already proves the vector path).

## Success criteria

- `P02.TicketTools` launches the knowledge server as a second stdio MCP client transport (`Command = dotnet run --project src/P12.McpKnowledgeServer`), merges its tool with the existing filesystem tools, and the agent answers "what's the VPN policy?" grounded in the corpus.
- Scorer unit test: known `password-expiry` doc ranks first for "password expired".
- Trace shows the tool invocation spanning the child MCP server process.
- Protocol integrity: zero writes to stdout by the server except the JSON-RPC stream (assert in code review / run log).

## Non-goals

HTTP transport, Tasks (SEP-2663), sampling/elicitation/MRTR — untouched. No embeddings in the server (P04 owns the vector variant).

## Verified SDK surface (ModelContextProtocol 2.2.0 — same package P02 pins)

```csharp
// server Program.cs (console):
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdout = protocol
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

[McpServerToolType]
public static class KnowledgeTools
{
    [McpServerTool, Description("Search the HelpDeskHQ handbook.")]
    public static string SearchKnowledge(string query, int maxResults = 3) => ...;
}
```
Client: `McpClient.CreateAsync(new StdioClientTransport(new() { Name, Command, Arguments }))` (SDK 2.x shape already in P02 `Program.cs` — the MAF docs still show stale `McpClientFactory`). MAF side unchanged: `McpClientTool : AIFunction` passes into `ChatOptions.Tools`.

Risk: stdout poisoning (any `Console.WriteLine` corrupts the protocol stream); corpus path resolution from a child working dir.

## Resources

- SDK: https://github.com/modelcontextprotocol/csharp-sdk (v2.2.0, spec 2026-07-28) · https://csharp.sdk.modelcontextprotocol.io/v1/concepts/getting-started.html
- MAF: https://learn.microsoft.com/en-us/agent-framework/agents/tools/local-mcp-tools?pivots=programming-language-csharp
- v2.0 breaking changes: https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/