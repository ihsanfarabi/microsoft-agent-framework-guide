# SPEC — P02: TicketTools (Function Tools + MCP)

**Tier:** Basic · **Estimate:** 4–5 hours · **Depends on:** P01

## Goal

Agent that acts, not just talks: manages tickets conversationally via function
tools, then via a second tool source — an MCP server. Tool loop visible in
traces. First "real" HelpDeskHQ feature.

## Concepts learned

- `AIFunctionFactory.Create` function tools with typed parameters
- `ChatClientBuilder(...).UseFunctionInvocation()` — the tool loop pipeline
- Tool-call → execute → result → second model turn, in traces
- MCP C# SDK: `McpClient`, `ListToolsAsync()`, tools as `AITool`

## Requirements

1. `P02.TicketTools` console project referencing `MafDemo.Core`.
2. `TicketToolFunctions` class wrapping `ITicketStore`: `CreateTicketAsync(title, description, priority)`, `ListTicketsAsync()`, `UpdateTicketStatusAsync(id, status)`, `AddTicketNoteAsync(id, note)` — xUnit-tested against `InMemoryTicketStore`, no LLM in tests.
3. Agent `TicketBot`: instructions + all four tools; one scripted scenario "file a ticket for my broken VPN" creates a ticket and echoes the ID.
4. OTel console traces show the function-call span (model call, tool, model call).
5. One MCP server via stdio (filesystem server on a sandbox dir) — its tools merged into the agent's toolset.

## Success criteria

- "File a ticket for my broken VPN, priority high" → ticket created, agent reads back ID and status.
- "List my tickets" → shows the ticket.
- Trace shows tool invocation spans between model calls.
- MCP tool (e.g. list sandbox files) callable through the agent.
- Tool unit tests pass.

## Stretch

- Write own MCP server in C# (ModelContextProtocol SDK) exposing `search_knowledge` over the handbook corpus — forward link to P04.

## Resources

- Tools: https://learn.microsoft.com/en-us/agent-framework/agents/tools
- MCP .NET integration: https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/tools/mcp
- Samples: https://github.com/microsoft/agent-framework-samples