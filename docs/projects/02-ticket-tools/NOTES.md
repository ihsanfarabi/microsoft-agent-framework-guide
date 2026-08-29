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