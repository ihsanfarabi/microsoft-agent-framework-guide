# P07: ResolutionWorkflow — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ticket resolution graph workflow with agent nodes, typed HITL approval port, and checkpoint resume across process restart.

**Architecture:** MAF graph workflow: code executors for routing/state, agent-backed executors for triage + diagnosis, `RequestPort` for approval. Console host runs the streaming event loop and answers requests. `ApprovalPolicy` (pure) decides escalation + note formatting. Store mutations are the observable outcomes.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` (prerelease), `OllamaSharp`, xUnit.

**Spec:** `docs/projects/07-resolution-workflow/SPEC.md`

## Global Constraints

- Verified C# workflow API (HITL doc, 2026-08): `RequestPort.Create<TReq,TResp>("id")`, `new WorkflowBuilder(start).AddEdge(a, b).WithOutputFrom(x).Build()`, `class X : Executor<T>("id")` overriding `HandleAsync(T, IWorkflowContext, CancellationToken)`, `context.SendMessageAsync(...)`, `context.YieldOutputAsync(...)`, `InProcessExecution.RunStreamingAsync(workflow, input)` → `StreamingRun`, `handle.WatchStreamAsync()`, `RequestInfoEvent.Request.CreateResponse(v)`, `handle.SendResponseAsync(...)`. If names error, copy from https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop.
- Message types flowing on edges: one `record TicketContext(...)` shared by all executors (verified generic `Executor<T>`).
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: TicketContext + ApprovalPolicy (TDD)

**Files:**
- Create: `src/P07.ResolutionWorkflow/P07.ResolutionWorkflow.csproj`, `TicketContext.cs`, `ApprovalPolicy.cs`
- Test: `tests/P07.ResolutionWorkflow.Tests/P07.ResolutionWorkflow.Tests.csproj`

**Interfaces:**
- Produces (all later tasks consume):
  - `record TicketContext(Guid TicketId, string Title, string Description, TicketPriority Priority, string Triage, string Diagnosis, string? ProposedFix, string? OperatorNote)`
  - `record FixApprovalRequest(Guid TicketId, string ProposedFix);`
  - `record ApprovalDecision(bool Approved, string Note);`
  - `static class ApprovalPolicy { bool NeedsEscalation(TicketPriority p); string ResolutionNote(TicketContext ctx, ApprovalDecision d); string RejectionNote(ApprovalDecision d); }`

- [ ] **Step 1: Scaffold**

```bash
dotnet new console -n P07.ResolutionWorkflow -o src/P07.ResolutionWorkflow -f net10.0
dotnet sln add src/P07.ResolutionWorkflow
dotnet add src/P07.ResolutionWorkflow reference src/MafDemo.Core
dotnet add src/P07.ResolutionWorkflow package Microsoft.Agents.AI --prerelease
dotnet add src/P07.ResolutionWorkflow package OllamaSharp
dotnet new xunit -n P07.ResolutionWorkflow.Tests -o tests/P07.ResolutionWorkflow.Tests -f net10.0
dotnet sln add tests/P07.ResolutionWorkflow.Tests
dotnet add tests/P07.ResolutionWorkflow.Tests reference src/P07.ResolutionWorkflow src/MafDemo.Core
```

- [ ] **Step 2: Write failing tests**

```csharp
public class ApprovalPolicyTests
{
    [Fact]
    public void Critical_needs_escalation()
        => Assert.True(ApprovalPolicy.NeedsEscalation(TicketPriority.Critical));

    [Theory]
    [InlineData(TicketPriority.Low)] [InlineData(TicketPriority.Normal)] [InlineData(TicketPriority.High)]
    public void Others_do_not(TicketPriority p) => Assert.False(ApprovalPolicy.NeedsEscalation(p));

    [Fact]
    public void Resolution_note_contains_diagnosis_and_fix()
    {
        var ctx = new TicketContext(Guid.NewGuid(), "VPN", "down", TicketPriority.High, "network", "router restart", "restart router", null);
        var note = ApprovalPolicy.ResolutionNote(ctx, new ApprovalDecision(true, "ok"));
        Assert.Contains("restart router", note);
        Assert.Contains("ok", note);
    }
}
```

- [ ] **Step 3: Run, verify FAIL** — `dotnet test`

- [ ] **Step 4: Implement records + policy** — trivial pure functions.

- [ ] **Step 5: Run, verify PASS. Commit** — `feat(p07): ticket context + approval policy`

### Task 2: Executors (graph nodes)

**Files:**
- Create: `src/P07.ResolutionWorkflow/Executors/TriageExecutor.cs`, `DiagnosticExecutor.cs`, `EscalationExecutor.cs`, `ResolutionExecutor.cs`, `ApprovalRequestExecutor.cs`
- Modify: `src/P07.ResolutionWorkflow/Program.cs`

**Interfaces:**
- Consumes: `TicketContext`, specialists/agents from P06 (`agent.RunAsync(text)` inside `HandleAsync`), `ITicketStore`
- Produces: `Workflow` graph wired in `Program.MainWorkflow()` (used by Tasks 3–4)

- [ ] **Step 1: Agent-backed executors** — wrap P06 specialist triage + a diagnostic prompt:

```csharp
internal sealed class TriageExecutor(AIAgent triageAgent) : Executor<TicketContext>("Triage")
{
    public override async ValueTask HandleAsync(TicketContext ctx, IWorkflowContext context, CancellationToken ct = default)
    {
        var reply = await triageAgent.RunAsync($"Classify in one word (network/software/hardware): {ctx.Title}: {ctx.Description}", cancellationToken: ct);
        var triaged = ctx with { Triage = reply.Text };
        await context.SendMessageAsync(triaged, cancellationToken: ct);
    }
}
```
(`reply.Text` member + `RunAsync` overload — verify against P01/P06 code.) `DiagnosticExecutor` similar: agent proposes fix, fills `ProposedFix`.

- [ ] **Step 2: `ApprovalRequestExecutor`** — routes by priority and sends the request:

```csharp
internal sealed class ApprovalRequestExecutor(RequestPort<FixApprovalRequest, ApprovalDecision> port) : Executor<TicketContext>("FixApproval")
{
    public override async ValueTask HandleAsync(TicketContext ctx, IWorkflowContext context, CancellationToken ct = default)
    {
        if (ctx.Priority == TicketPriority.Critical)
            await context.SendMessageAsync(ctx with { ProposedFix = $"ESCALATED: {ctx.ProposedFix}" }, "Escalation", ct);  // target-name overload: verify
        else
            await port.SendRequestAsync(new FixApprovalRequest(ctx.TicketId, ctx.ProposedFix!), ct);   // send method name: verify (doc shows RequestPort as builder start + executor SendMessage)
    }
}
```
Doc pattern note: the HITL sample wires `WorkflowBuilder(numberRequestPort)` — port receives requests FROM an executor via a message edge (`AddEdge(judgeExecutor, numberRequestPort)`). Follow the doc's edge-to-port wiring, not an invented `SendRequestAsync`.

- [ ] **Step 3: `EscalationExecutor`** — agent node: "You are the escalation engineer. Refine this fix for a Critical incident: ..." → updated `ProposedFix`, then sends to the port edge.

- [ ] **Step 4: `ResolutionExecutor`** — consumes `ApprovalDecision` responses (per doc, executor handling the response re-receives original request — model your response handler as `Executor<FixApprovalRequest>` variant or follow doc's `JudgeExecutor` re-entry pattern), then:

```csharp
if (decision.Approved)
    { await store.UpdateStatusAsync(ctx.TicketId, TicketStatus.Resolved); await store.AddNoteAsync(ctx.TicketId, ApprovalPolicy.ResolutionNote(ctx, decision)); }
else
    { await store.UpdateStatusAsync(ctx.TicketId, TicketStatus.InProgress); await store.AddNoteAsync(ctx.TicketId, ApprovalPolicy.RejectionNote(decision)); }
await context.YieldOutputAsync($"ticket {ctx.TicketId}: {(decision.Approved ? "resolved" : "rejected — in progress")}", ct);
```

- [ ] **Step 5: Wire graph in Program**

```csharp
var port = RequestPort.Create<FixApprovalRequest, ApprovalDecision>("FixApproval");
var workflow = new WorkflowBuilder(port)
    .AddEdge(port, resolutionExecutor)          // pending-approval path: follow doc wiring
    // triage -> diagnose -> approval -> (escalation) -> resolution
    .WithOutputFrom(resolutionExecutor)
    .Build();
```
Graph shape follows the doc sample's edge-to-port topology; adjust to actual sample.

- [ ] **Step 6: Compile check** — `dotnet build`. Commit — `feat(p07): workflow executors + graph`

### Task 3: HITL event loop + batch scenario

**Files:**
- Modify: `src/P07.ResolutionWorkflow/Program.cs`

- [ ] **Step 1: Run loop** — verified pattern:

```csharp
await using StreamingRun handle = await InProcessExecution.RunStreamingAsync(workflow, ticketCtx);
await foreach (WorkflowEvent evt in handle.WatchStreamAsync())
{
    switch (evt)
    {
        case RequestInfoEvent reqEvt:
            Console.WriteLine($"Approve fix for ticket {((FixApprovalRequest)reqEvt.Request.Data).TicketId}: {((FixApprovalRequest)reqEvt.Request.Data).ProposedFix}");
            Console.Write("approve? (y/n + note): ");
            var line = Console.ReadLine() ?? "n";
            bool ok = line.StartsWith('y');
            var note = line.Length > 1 ? line[1..].Trim() : "";
            await handle.SendResponseAsync(reqEvt.Request.CreateResponse(new ApprovalDecision(ok, note)));
            break;
        case WorkflowOutputEvent outEvt:
            Console.WriteLine($"[done] {outEvt.Data}");
            break;
    }
}
```
(`Request.Data` member — verify against doc sample.)

- [ ] **Step 2: Batch scenario** — seed 3 tickets (Wi-Fi High, Excel Normal, laptop-encrypted Critical), run all three through the workflow in one session. Approve 2, reject 1.

- [ ] **Step 3: Assert outcomes** — after run: two tickets `Resolved` with notes, one `InProgress` with rejection note. Print store state; verify Critical trace shows Escalation span.

- [ ] **Step 4: Commit** — `feat(p07): hitl approval loop + batch scenario`

### Task 4: Checkpoint kill-and-resume

**Files:**
- Modify: `src/P07.ResolutionWorkflow/Program.cs`
- Create: `docs/projects/07-resolution-workflow/NOTES.md` (checkpoint API findings)

- [ ] **Step 1: Research checkpoint API** — fetch https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints. Record in NOTES.md: checkpoint save/restore/`checkpoint_id`+responses resume API for C# in-process runs.

- [ ] **Step 2: Implement checkpoint at approval pause** — save checkpoint when the `RequestInfoEvent` is emitted, before answering; Ctrl-C kill without answering.

- [ ] **Step 3: Restore path** — restart process, restore from checkpoint, confirm pending request re-emitted (doc: pending requests saved in checkpoint state), answer it, workflow completes; ticket state correct.

- [ ] **Step 4: If in-process checkpointing is not available for this topology** — fallback POC: serialize `TicketContext` batch + which stage each is in to `checkpoint.json`, restart re-drives from that stage; note in NOTES.md that true durable execution arrives in P09.

- [ ] **Step 5: Commit** — `feat(p07): checkpoint kill-and-resume`

### Task 5: NOTES

**Files:**
- Modify: `docs/projects/07-resolution-workflow/NOTES.md` (started in Task 4 — append, don't overwrite)

- [ ] **Step 1: NOTES.md** — bullets: graph vs orchestration (vs P06 handoff), where HITL state lives, checkpoint mechanism used, failure modes.
- [ ] **Step 2: Commit** — `docs(p07): learning notes`