# P15 DistributedWorkflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One local `WorkflowBuilder` graph whose middle nodes are remote A2A
agents (DiagnosisAgent + the existing P09 InventoryAgent), with a conditional
edge skipping the inventory hop — three processes in one trace.

**Architecture:** Two new projects: a DiagnosisAgentService A2A host (clone of
InventoryAgentService) and an OrchestratorHost console running the graph.
Reuse P09 client/server plumbing verbatim; no durability.

**Tech Stack:** `Microsoft.Agents.AI.Workflows` 1.19.0, `Microsoft.Agents.AI.A2A`
1.19.0-preview.260822.1, ASP.NET Core hosting, Ollama.

**Spec:** `docs/projects/15-distributed-workflow/SPEC.md`

## Global Constraints

- Same global constraints as P11-P14 (see P11 plan). Do NOT modify
  `src/P09.InventoryAgentService`. Ports: 5199 (P09, existing), 5200 (new).

---

### Task 1: `P15.DiagnosisAgentService` — second A2A service on :5200

**Files:** Create `src/P15.DiagnosisAgentService/P15.DiagnosisAgentService.csproj`, `Program.cs`, `appsettings.json`.

**Interfaces:**
- Consumes: `MafDemo.AgentCommon.OllamaChat.Create()`.
- Produces: A2A service whose agent `Name = "DiagnosisAgent"`, card at
  `http://localhost:5200/.well-known/agent-card.json`, messages at
  `/a2a/diagnosis/message:send`. Answers are free-form (`string`) — **no
  session state**: the orchestrator owns sequencing.

- [x] **Step 1: create** — copy `P09.InventoryAgentService/Program.cs`, rename
  agent to `DiagnosisAgent`, instructions "You diagnose IT tickets. Answer in
  ≤ 3 sentences. If the diagnosis mentions hardware, say NEEDS-HARDWARE." Port
  5200 (`ASPNETCORE_URLS` + launchSettings, per P10 lesson — no `UseUrls`).
- [x] **Step 2: verify** — run, curl `POST http://localhost:5200/a2a/diagnosis/message:send`, expect diagnosis text. Stop. Commit `feat(p15): DiagnosisAgent A2A service on :5200`.

### Task 2: OrchestratorHost — graph with two remote hops

**Files:** Create `src/P15.OrchestratorHost/{csproj, Program.cs, Executors/TriageExecutor.cs, Executors/ReportExecutor.cs, appsettings.json}` + slnx.

**Interfaces:**
- Consumes: `P09.HelpDeskClient`'s resolver pattern; P07 conditional-edge style.
- Produces: console running two scenarios.

- [x] **Step 1: resolve both remote agents**
```csharp
AIAgent diagnosis = await new A2ACardResolver(new Uri("http://localhost:5200")).GetAIAgentAsync();
AIAgent inventory = await new A2ACardResolver(new Uri("http://localhost:5199")).GetAIAgentAsync();
```
- [x] **Step 2: build the graph** — `TriageExecutor : Executor` (P07 pattern:
  `Ingest(string, IWorkflowContext, CancellationToken)` emitting
  `ChatMessage`), then
```csharp
var workflow = new WorkflowBuilder(triage)
    .AddEdge(triage, diagnosis)                                   // implicit AIAgent→ExecutorBinding
    .AddEdge<AgentResponse>(diagnosis, inventory, r => ContainsHardware(r))
    .AddEdge<AgentResponse>(diagnosis, report, r => !ContainsHardware(r))
    .AddEdge(inventory, report)
    .WithOutputFrom(inventory, report)
    .Build();
var run = await InProcessExecution.RunAsync(workflow, "ticket: laptop won't boot");
```
(Exact input/output types resolved against `ChatProtocol` at implementation;
fallback wrapper `Executor` documented in SPEC if `AddEdge` inference fights.)
- [x] **Step 3: payoff demo** — run ticket A (software-only: skips inventory),
  ticket B (hardware: both hops). Print which processes each hop hit. Verify
  in Aspire dashboard trace: one workflow, calls to two different A2A targets.
- [x] **Step 4: commit** `feat(p15): workflow graph across two remote A2A agents`.

### Task 3: Failure is visible

- [x] **Step 1:** demo = run hardware ticket with inventory service stopped →
  workflow fails with exception naming the A2A endpoint; assert via console
  output, and in streaming mode surfaced as `WorkflowErrorEvent`.
- [x] **Step 2:** handle-or-propagate decision documented (choose propagate —
  retries are a durable-host concern, P16 note). Commit `feat(p15): kill-service failure demo`.

### Task 4: Docs + portfolio

- [x] **Step 1:** `docs/projects/15-distributed-workflow/NOTES.md` — by-contract
  vs by-example gap; ChatProtocol boundary findings (payload types that broke,
  the shape that worked); why durability is deferred.
- [x] **Step 2:** README ladder + PORTFOLIO row (+1 line in architecture
  diagram if mermaid edit stays small). Full suite green.
- [x] **Step 3: commit** `docs(p15): notes + portfolio entries`.