# P06: TriageComposition — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three specialist agents composed two ways — as tools under a triage agent, then via handoff orchestration — same scenarios, compared.

**Architecture:** `Specialists` factory builds the three agents (shared chat client, distinct instructions + tools). Phase 1: TriageAgent gets converted specialists as tools. Phase 2: handoff workflow via orchestration package (names verified in Task 4). Console scenarios drive both phases; traces go to Aspire.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` (prerelease), orchestration package (verify exact name in Task 4), `OllamaSharp`, xUnit.

**Spec:** `docs/projects/06-triage-composition/SPEC.md`

## Global Constraints

- Model `glm-5.3-flash:cloud` via `OllamaChat.Create()`; every agent using tools needs `UseFunctionInvocation()` chat client.
- Specialist routing is model-driven — the lever is tool/agent description quality, not code.
- Two doc-verify steps in this plan (agents-as-tool extension name, orchestration builder names). Copy exact names; fix this plan if drifted.
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: Specialists (testable tool layer)

**Files:**
- Create: `src/P06.TriageComposition/P06.TriageComposition.csproj`, `Specialists.cs`, `Program.cs`
- Test: `tests/P06.TriageComposition.Tests/P06.TriageComposition.Tests.csproj`

**Interfaces:**
- Consumes: `ITicketStore`, handbook retrieval (`search_handbook`) from P04, `OllamaChat.Create`
- Produces: `static class Specialists { NetworkSpecialist(); SoftwareSpecialist(); HardwareSpecialist(); }` each returning a `ChatClientAgent`; `static class SpecialistTools { SearchHandbook(...); GetTicket(...); }`

- [x] **Step 1: Scaffold**

```bash
dotnet new console -n P06.TriageComposition -o src/P06.TriageComposition -f net10.0
dotnet sln add src/P06.TriageComposition
dotnet add src/P06.TriageComposition reference src/MafDemo.Core
dotnet add src/P06.TriageComposition package Microsoft.Agents.AI --prerelease
dotnet add src/P06.TriageComposition package OllamaSharp
dotnet new xunit -n P06.TriageComposition.Tests -o tests/P06.TriageComposition.Tests -f net10.0
dotnet sln add tests/P06.TriageComposition.Tests
dotnet add tests/P06.TriageComposition.Tests reference src/P06.TriageComposition src/MafDemo.Core
```

- [x] **Step 2: Tool functions** — port `search_handbook` (P04 retrieval) and `get_ticket` (P02) into `SpecialistTools`. Tests: `SearchHandbook("vpn")` returns non-empty top chunk; `GetTicket` round-trip against `InMemoryTicketStore`.

- [x] **Step 3: Run tests, verify PASS** — `dotnet test`

- [x] **Step 4: Specialist factories** — shared `IChatClient` (one `ChatClientBuilder(...).UseFunctionInvocation()` per specialist is fine), distinct instructions, e.g. NetworkSpecialist: `"You are HelpDeskHQ's network specialist. Diagnose connectivity, Wi-Fi, VPN issues using the handbook. Answer concisely with steps."` Hardware/Software analogues with their tools.

- [x] **Step 5: Smoke run** — direct `RunAsync` on each specialist with its scenario prompt; expect grounded answers.

- [x] **Step 6: Commit** — `feat(p06): three specialist agents`

### Task 2: Agents-as-tools triage

**Files:**
- Create: `src/P06.TriageComposition/TriageAsTools.cs`
- Modify: `src/P06.TriageComposition/Program.cs`

**Interfaces:**
- Produces: `TriageAsTools.Create() -> ChatClientAgent`

- [x] **Step 1: Find exact extension** — doc section "Using an agent as a function tool" at https://learn.microsoft.com/en-us/agent-framework/agents/tools. Best guess (verify!): `specialist.AsAITool(name: ..., description: ...)`. Write the exact method from the doc into NOTES.md before coding.

- [x] **Step 2: Compose triage agent**

```csharp
// sketch — replace AsAITool with verified name
var network = Specialists.NetworkSpecialist();
var software = Specialists.SoftwareSpecialist();
var hardware = Specialists.HardwareSpecialist();

var triage = new ChatClientAgent(
    new ChatClientBuilder(OllamaChat.Create()).UseFunctionInvocation().Build(),
    name: "TriageAgent",
    instructions: """
        You are HelpDeskHQ's front desk. Classify the user's IT problem, then
        delegate to exactly ONE specialist tool: network_connectivity (Wi-Fi,
        VPN, internet), software_support (apps crashing, install, licenses),
        hardware_support (laptop, charger, peripherals). Return the specialist's
        answer to the user, prefixed with which specialist handled it.
        """)
{
    Tools = [network.AsAITool(name: "network_connectivity", description: "Wi-Fi, VPN and connectivity issues"),
             software.AsAITool(name: "software_support", description: "Application crashes, installs, licensing"),
             hardware.AsAITool(name: "hardware_support", description: "Laptops, chargers, physical devices")]
};
```
(Tools member placement — verify against P02/agents/tools doc sample.)

- [x] **Step 3: Run all three scenarios** — Wi-Fi / Excel / laptop prompts. Expect answers prefixed by correct specialist.

- [x] **Step 4: Trace check** — Aspire: expect nested spans (triage model call → specialist agent run → specialist model call → tool). Record which specialist tool was invoked per scenario.

- [x] **Step 5: Commit** — `feat(p06): triage via agents-as-tools`

### Task 3: Handoff orchestration

**Files:**
- Create: `src/P06.TriageComposition/TriageHandoff.cs`
- Modify: `src/P06.TriageComposition/Program.cs`

**Interfaces:**
- Produces: `TriageHandoff.Create()` returning runnable workflow (exact type from doc)

- [x] **Step 1: Verify orchestration API** — fetch https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff. Record in NOTES.md: NuGet package name (best guess: `Microsoft.Agents.AI.Orchestration`), builder class (best guess: `HandoffWorkflowBuilder` or `OrchestrationBuilder`), run method.

- [x] **Step 2: Build handoff workflow** — sketch (replace with verified types):

```csharp
var workflow = new HandoffWorkflowBuilder()            // verify name
    .AddAgent(triageAgent, handoffTargets: [software, hardware])  // triage can hand off
    .AddAgent(network, handoffTargets: [triage])
    .AddAgent(software, handoffTargets: [triage])
    .AddAgent(hardware, handoffTargets: [triage])
    .Build();
```
Handoff instructions: each agent gets a handoff tool per target ("transfer to X when the problem is Y").

- [x] **Step 3: Run same three scenarios** — interactive console loop per handoff doc (handoff returns control to user between agents).

- [x] **Step 4: Trace check** — compare spans vs Phase 1: who holds conversation, how many model calls.

- [x] **Step 5: Commit** — `feat(p06): triage via handoff orchestration`

### Task 4: Comparison + notes

**Files:**
- Create: `docs/projects/06-triage-composition/NOTES.md`

- [x] **Step 1: NOTES.md table** — per scenario, per phase: model-call count (from spans), final-answer latency feel, routing correctness, context sharing (does specialist see full conversation?), failure mode observed.
- [x] **Step 2: Verdict** — 3 bullets: when agents-as-tools wins, when handoff wins, what surprised you.
- [x] **Step 3: Commit** — `docs(p06): pattern comparison notes`