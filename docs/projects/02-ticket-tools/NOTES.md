# P02: TicketTools — Notes

## Task 3: Tool-loop trace (OTel console exporter)

One live run (`dotnet run --project src/P02.TicketTools`, model `glm-5.3-flash:cloud` via Ollama at localhost:11434) produced 14 spans: each `RunAsync` call gets its own trace rooted at an `orchestrate_tools` span, with the tool loop nested inside it. The console exporter prints spans on end, so children appear before their parent.

Span sequence per run (children in causal order):

- Run 1 "File a ticket…VPN, priority high" — `chat glm-5.3-flash:cloud` → `execute_tool CreateTicket` → `chat glm-5.3-flash:cloud`, all under `orchestrate_tools`
- Run 2 "List my tickets" — `chat glm-5.3-flash:cloud` → `execute_tool ListTickets` → `chat glm-5.3-flash:cloud`, under `orchestrate_tools`
- Run 3 "Mark the VPN ticket resolved" — `chat glm-5.3-flash:cloud` → `execute_tool ListTickets` → `chat glm-5.3-flash:cloud` → `execute_tool UpdateTicketStatus` → `chat glm-5.3-flash:cloud`, under `orchestrate_tools` (two tool round-trips in one loop)

Function-call span names observed:

- `chat glm-5.3-flash:cloud` (Kind: Client, `gen_ai.operation.name: chat`) — one per model request, carrying `gen_ai.tool.definitions` with all four tools and `gen_ai.usage.*` token counts
- `execute_tool CreateTicket`, `execute_tool ListTickets`, `execute_tool UpdateTicketStatus` (Kind: Internal, `gen_ai.operation.name: execute_tool`, `gen_ai.tool.name` / `gen_ai.tool.call.id` tags) — `AddTicketNote` was defined but never called in this scenario
- `orchestrate_tools` — the agent-level parent span that wraps the whole loop

Tool loop in traces: each user turn is one trace where the model `chat` span that requests a tool call is followed by a sub-millisecond `execute_tool` span, then a second `chat` span that consumes the tool result and answers — request → tool → request, once per tool round-trip, all nested under `orchestrate_tools`.

## Task 4: MCP server tools — exact API shape

Package: `ModelContextProtocol` 2.2.0 (stable, no `--prerelease` needed; splits into `ModelContextProtocol` + `ModelContextProtocol.Core`). With SDK 2.x the client creation is `await McpClient.CreateAsync(new StdioClientTransport(new() { Name = "...", Command = "npx", Arguments = ["-y", "@modelcontextprotocol/server-filesystem", <abs sandbox path>] }))` (namespace `ModelContextProtocol.Client`) — the `McpClientFactory.CreateAsync(...)` shape shown in the Agent Framework doc is the older 1.x SDK API and does not exist in 2.x. `await mcpClient.ListToolsAsync()` returns `IList<McpClientTool>`, which slots straight into `ChatOptions.Tools` (or `ChatClientAgentOptions.ChatOptions.Tools`) alongside `AIFunctionFactory.Create` tools — `McpClientTool` is an `AITool`, so no cast is required (`mcpTools.Cast<AITool>()` also compiles). The client is `await using` so the spawned server process is disposed on exit. Verified against https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-mcp-client (the Agent Framework MCP page at .../agents/tools/local-mcp-tools still shows the 1.x `McpClientFactory` sample).

## Task 5: Wrap-up

**How the model chose tools.** All three scripted runs picked the correct tool on the first chat request: "file a ticket… priority high" mapped straight to `CreateTicket`, "list my tickets" to `ListTickets`, and "mark the VPN ticket resolved" to `ListTickets` followed by `UpdateTicketStatus` — in run 3 the model re-listed the tickets first to recover the actual ticket ID before updating it, rather than guessing. It never invented a ticket ID; the agent instructions explicitly forbid that, and the model complied across every run.

**Function tools vs MCP tools, from the app's perspective.** Function tools are local C# methods wrapped with `AIFunctionFactory.Create` and registered directly in `ChatOptions.Tools` — fully in-process, no extra moving parts. MCP tools are discovered at runtime from a separate stdio server process (`npx @modelcontextprotocol/server-filesystem`): the app creates the client (`await using McpClient.CreateAsync(new StdioClientTransport(...))`), calls `ListToolsAsync()`, and merges the returned tools into the same `Tools` list. From the model's point of view the two kinds are identical — both are just `AITool` entries with JSON schemas it can call. From the app's point of view MCP adds a child process to spawn/dispose and a discovery step at startup, but zero in-proc code for each tool the server exposes.

**One trace observation.** The span sequence per turn is `chat` → `execute_tool <Name>` → `chat`, with the client-side `chat` span carrying `gen_ai.tool.definitions` (all tools, function and MCP kinds merged into one list) and `gen_ai.usage.*` token counts; `execute_tool` spans are sub-millisecond Internal spans. Run 3 showed two full round-trips inside one loop (list, then update), so the multi-step recover-ID-then-update behavior is visible end-to-end in the traces.

**Stretch — skipped.** A custom C# MCP server exposing `search_knowledge` over a handbook corpus was not built; it remains future work and a seed for P04.
