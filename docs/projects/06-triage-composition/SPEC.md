# SPEC — P06: TriageComposition (Agents-as-Tools + Handoff Orchestration)

**Tier:** Intermediate · **Estimate:** 4–5 hours · **Depends on:** P05

## Goal

First multi-agent project. HelpDeskHQ grows specialists: Network, Software, Hardware. Build the same triage scenario twice — specialists as tools called by a triage agent, then handoff orchestration — and compare the two patterns.

## Concepts learned

- Agents-as-tools: inner agent converted to a function tool, model-driven routing
- Tool description quality = routing quality; context isolation between outer/inner agent
- Handoff orchestration: framework-managed transfer of control between agents
- Multi-agent traces in Aspire (nested spans)

## Requirements

1. `P06.TriageComposition` console app.
2. Three specialist agents, each: own instructions + 1–2 tools from earlier projects (NetworkSpecialist: `search_handbook`; SoftwareSpecialist: `search_handbook` + `get_ticket`; HardwareSpecialist: `search_handbook`).
3. **Phase 1 — agents-as-tools**: TriageAgent holds all three specialists as tools, decides delegation itself, synthesizes final answer.
4. **Phase 2 — handoff orchestration**: same specialists via MAF handoff pattern (exact builder per docs); conversation transfers between triage and specialist, interactive with the console user.
5. **Phase 3 — comparison** in NOTES.md: control flow, context sharing, latency, when each pattern wins.
6. Scenarios: `"My Wi-Fi drops every 5 minutes"` → NetworkSpecialist; `"Excel crashes on open"` → SoftwareSpecialist; `"Laptop won't charge"` → HardwareSpecialist.

## Success criteria

- Phase 1: all three scenarios routed to correct specialist (trace proves which tool/agent ran); final answer cites specialist output.
- Phase 2: same three scenarios complete via handoff; specialist holds the conversation.
- NOTES.md comparison table filled with observed facts (span counts, turn counts), not theory.

## Stretch

- Fourth specialist (Security) and an ambiguous prompt that should trigger clarification — compare how each pattern handles it.
- Group-chat orchestration variant: specialists discuss one hard ticket.

## Resources

- Agents as tools: https://learn.microsoft.com/en-us/agent-framework/journey/agents-as-tools
- Agent-as-tool API (.NET section "Using an agent as a function tool"): https://learn.microsoft.com/en-us/agent-framework/agents/tools
- Handoff orchestration: https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff
- Orchestration overview: https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations