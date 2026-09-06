# P06: TriageComposition — Notes

## Task 4: pattern comparison — agents-as-tools vs handoff, measured

**Method.** Both phases re-run back-to-back from the same binary (`-- as-tools`, then default `handoff`), same machine, same Ollama `glm-5.3-flash:cloud`, minutes apart, against a keyed dashboard instance — see `.superpowers` task-2-tracecheck.md (untracked) for the recipe. The unlock, inline:

```bash
docker run --rm -d -p 18888:18888 -p 4317:18889 --name aspire-dashboard \
  -e DASHBOARD__FRONTEND__AUTHMODE=ApiKey -e DASHBOARD__FRONTEND__APIKEYDOCUMENT=<key> \
  -e DASHBOARD__OTLP__AUTHMODE=ApiKey -e DASHBOARD__OTLP__PRIMARYAPIKEY=<key> \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

(4317→18889 enables the OTLP telemetry API; set `<key>` to a generated value, never a literal in-repo secret); traces read via its `/api/telemetry/traces` endpoint with the instance's API key. Traces captured fresh per run and diffed against an 18-trace pre-run snapshot so only this session's spans are counted. Per trace: wall = `max(endTimeUnixNano) - min(startTimeUnixNano)` over the trace's spans; model calls = `chat <model>` span count under scope `Experimental.Microsoft.Agents.AI`; routing = which `execute_tool <name>` appears (Phase 1) or which tool-definition set + chronology the trace carries (Phase 2). Phase 2's two traces per scenario are consecutive on the clock (triage trace ends, specialist starts 3–17 ms later), which is how they were paired with the console's `held by:` order (network → software → hardware). The spec's "interactive with the console user" seam for Phase 2 is implemented as control-return + merge loop (the caller merges the agents' messages and re-runs); the scenarios are scripted one-turn so span counts stay reproducible — Program.cs:114-118 documents the same.

### Phase 1 spans — agents-as-tools (3 traces, one per scenario)

| Scenario | Trace | Wall | Model calls | Tool calls (spans) |
|---|---|---|---|---|
| Wi-Fi | `06516183` | 7654 ms | 4 | `execute_tool network_connectivity` + 2 × `search_handbook` |
| Excel | `11505efc` | 9922 ms | 5 | `execute_tool software_support` + 2 × `search_handbook` |
| Laptop | `e9304268` | 8392 ms | 4 | `execute_tool hardware_support` + 2 × `search_handbook` |

Shape (all three): one flat `orchestrate_tools` root; child `chat` (triage routing, tool defs `network_connectivity, software_support, hardware_support`); child `execute_tool <specialist>` whose **parent is the triage run**; nested inside it a second `orchestrate_tools` (the specialist run) holding the specialist's chats (tool defs: its own tools only — `search_handbook`, plus `get_ticket` for software) and its `search_handbook` calls; then a final sibling `chat` back at triage level (synthesis, input tokens grown to 663–769 — it ingested the specialist's answer). Task 2's earlier run (4/5/4 calls, walls 8331/10629/9010 ms) reproduced here as 4/5/4 calls at 7654/9922/8392 ms — same call counts, ~10% lower walls.

### Phase 2 spans — handoff orchestration (6 traces, two per scenario)

| Scenario | Triage trace (wall, calls, in-tok) | Specialist trace (wall, calls, in-tok) | End-to-end wall |
|---|---|---|---|
| Wi-Fi | `3c7935bc` (1322 ms, 1 chat, 493 in / 80 out) | `a9dfd35c` (4738 ms, 2 chats, 1343 in / 336 out, 2 × `search_handbook`) | 6077 ms |
| Excel | `4c023643` (992 ms, 1 chat, 489 in / 76 out) | `c434cd61` (7732 ms, 3 chats, 2751 in / 704 out, 3 × `search_handbook`) | 8727 ms |
| Laptop | `28916af0` (936 ms, 1 chat, 490 in / 67 out) | `ac4478ac` (4589 ms, 3 chats, 2197 in / 423 out, 2 × `search_handbook`) | 5528 ms |

**What a handoff trace actually looks like, from the data.** No nesting anywhere. The transfer splits one user scenario into two unrelated `traceId`s: the triage run is its own root `orchestrate_tools` with exactly one `chat` whose `gen_ai.tool.definitions` are `handoff_to_1, handoff_to_2, handoff_to_3` — the framework's anonymized numbered handoff tools, not specialist names — and then the trace just ends. There is **no `execute_tool` span for the handoff call** in any of the six traces, and no child span under the triage run. The specialist starts a fresh root `orchestrate_tools` in a new trace, its chats carrying `handoff_to_1` (the declared back-to-triage edge) plus its own tools. Consequences, all observed: the dashboard shows handoff as two disconnected traces; nothing in a span *name* says which specialist received the handoff — the destination is only recoverable from the specialist trace's tool-definition set (`get_ticket` present ⇒ software), the clock ordering, and the console's `held by:` line. Routing in Phase 2 is provable by correlation, not by the trace itself.

### The comparison (observed, not theory)

| | Phase 1 (as-tools), per scenario Wi-Fi / Excel / Laptop | Phase 2 (handoff), same order |
|---|---|---|
| Model calls | 4 / 5 / 4 | 3 / 4 / 4 |
| Wall latency | 7654 / 9922 / 8392 ms | 6077 / 8727 / 5528 ms (end-to-end over both traces) |
| Routing correctness | proven in-span: `execute_tool network_connectivity` / `software_support` / `hardware_support` under the triage run — 3/3 correct | 3/3 correct, but proven off-span: tool-def fingerprint + chronology + console `held by: NetworkSpecialist / SoftwareSpecialist / HardwareSpecialist` |
| Control flow | triage owns the loop start to finish: one `orchestrate_tools` root, specialist run nested inside its tool-call span, triage synthesizes the final answer | ownership moves: triage's run ends at the handoff call (its trace closes); the specialist holds its own root run and answers directly; control returns to the caller, who re-runs if the conversation continues |
| Context sharing | triage prompt: 3 specialist tool defs (393–397 in-tok on the routing call); specialist sees only its query — first inner chat 210–279 in-tok, defs limited to its own tools (isolation at the tool-definition level is directly visible); triage then pays a synthesis call (663–769 in-tok) to relay the specialist's text | triage sees only the 3 anonymized handoff tools (489–493 in-tok) and no specialist tools at all; specialist is handed the broadcast user message + handoff instructions (first chat 375–444 in-tok) and owns the conversation from there — answer streams from the specialist, TriageAgent re-enters only if a specialist hands back |
| Failure modes observed | none this run: 3/3 routed, answers prefixed `**[Specialist]**` and handbook-grounded; cost is the extra synthesis call and the triage model juggling three tool descriptions | none this run: 3/3 held by the right specialist; the observed weaknesses are structural, not behavioral — transfer invisible in span names, scenario fragmented across 2 traceIds, handoff tool call leaves no telemetry trace of its own |

Cross-checks and honest caveats: every number above is one live run per phase (n=1; Ollama latency wobbles — Task 2's earlier run vs today's Phase 1 differed ~10% with identical call counts). `get_ticket` was defined for SoftwareSpecialist but the model never called it — in either phase, in either run. Per-scenario Phase 2 "end-to-end" is computed as specialist-trace end minus triage-trace start (inter-trace gaps measured 3–17 ms, so the sum of the two walls is within 0.3%). Console prefixes differed cosmetically between phases (as-tools: triage relays with a `[Handled by: …]`-style prefix; handoff: the specialist's own words are the answer) — that is a Task 3 console finding, not a span fact.

## Verified API facts (recorded per the brief; sources: task-2-report.md and task-3-report.md)

**Agents-as-tools (verified against the shipped `Microsoft.Agents.AI` 1.19.0 XML, then docs).**
- The brief's sketch `specialist.AsAITool(name:, description:)` **does not exist** in MAF 1.19.0.
- The real member: `AIAgentExtensions.AsAIFunction(this AIAgent, AIFunctionFactoryOptions? options = null, AgentSession? session = null)` → `Microsoft.Extensions.AI.AIFunction`. Lives in the main `Microsoft.Agents.AI` package (not `.Abstractions`). The wrapped function takes one query string, returns the agent's response text; with no session passed, a fresh `AgentSession` per call (one-shot semantics).
- Per-tool name/description customization is `AIFunctionFactoryOptions.{Name, Description}` from `Microsoft.Extensions.AI` (10.9.0 XML); here pinned as `network_connectivity` / `software_support` / `hardware_support`.
- Tool placement is `ChatClientAgentOptions.ChatOptions.Tools`; there is no object-initializer `Tools = [...]` on the agent.

**Handoff orchestration (verified against restored `Microsoft.Agents.AI.Workflows` 1.19.0 XML).**
- Handoff lives in `Microsoft.Agents.AI.Workflows` 1.19.0 (mirrors the main package's version line 1.0.0 → 1.19.0), not in a separate orchestration package.
- Verified builder shape: `AgentWorkflowBuilder.CreateHandoffBuilderWith(AIAgent initial)` → per-pair `WithHandoff(source, target, reason)` / `WithHandoffs(...)` → `.Build()` → `Workflow`. There is **no** `AddAgent(agent, handoffTargets: ...)` fluent chain (the brief's guess; nonexistent). Also present in 1.19.0 but unused: `WithHandoffInstructions`, `WithToolCallFilteringBehavior`, `EnableReturnToPrevious`, `WithAutonomousMode`, `WithTerminationCondition`, `AddParticipants`.
- Run pattern: `InProcessExecution.RunStreamingAsync(workflow, messages)` → `run.TrySendMessageAsync(new TurnToken(emitEvents: true))` → `foreach (WorkflowEvent evt in run.WatchStreamAsync())`; `AgentResponseUpdateEvent` carries `ExecutorId` (name + GUID suffix), terminal `WorkflowOutputEvent` → `output.As<List<ChatMessage>>()`.
- Mechanism (confirmed by the spans above): one `HandoffAgentExecutor` per agent; a handoff tool per declared target is injected into each agent (span-observed names `handoff_to_1..N`); a run ends when the holding agent answers without calling it — handoff is interactive by design, and the caller continues by re-running with merged history. Tool-call/result content is filtered from forwarded history; agents do not share sessions.

## Verdict

- **Reach for agents-as-tools when you want one controllable loop and one trace.** Routing lands in the span names (`execute_tool network_connectivity`), the whole scenario is a single nested `traceId` — trivially debuggable — and the triage's synthesis step gives one place to shape the answer. It costs 1/1/0 extra model calls per scenario (4/5/4 vs 3/4/4 measured) — the +1 in the first two scenarios is the synthesis round; the third's specialist simply looped one chat shorter.
- **Reach for handoff when ownership (not delegation) is the point — and it was measurably faster here.** The specialist's answer streams straight to the user with no relay call, and end-to-end walls came out lower in all three scenarios (6077/8727/5528 vs 7654/9922/8392 ms, n=1). The price is observability and naming: the transfer produces no `execute_tool` span, splits the scenario across two unrelated traces, and the injected tools are anonymous `handoff_to_1..3` — you cannot tell from telemetry alone which agent picked up the conversation.
- **The surprise: telemetry inverts the complexity story.** The "simpler" pattern (handoff, framework-managed) is the one that fragments traces and hides its routing decision, while the pattern with the scarier-looking nested structure is the one that answers every "who ran what?" question from spans alone. Second surprise: the anonymized `handoff_to_N` tool names in the spans — descriptions carry the semantics at build time, but what lands in telemetry is numbers, so anything auditing routing later needs the builder mapping or the tool-definition fingerprint.
