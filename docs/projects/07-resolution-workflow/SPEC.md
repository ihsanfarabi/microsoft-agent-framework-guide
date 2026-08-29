# SPEC — P07: ResolutionWorkflow (Graph Workflow + HITL + Checkpoint)

**Tier:** Intermediate · **Estimate:** 5–6 hours · **Depends on:** P06

## Goal

First graph workflow: the ticket resolution pipeline as explicit, versioned code. Tickets flow Triage → Diagnose → Approve (human) → Resolve, with Critical tickets adding an escalation step. Human approval is a first-class workflow pause, and an in-flight workflow survives a process restart via checkpoint.

## Concepts learned

- `WorkflowBuilder` graph API: executors, edges, fan-out/fan-in, output ports
- Custom `Executor<T>` — code nodes and agent nodes in one graph
- `RequestPort<TReq, TResp>` — typed HITL channel, `RequestInfoEvent`, `SendResponseAsync`
- Checkpoints: save mid-run, restore and resume
- Conditional routing by ticket priority

## Requirements

1. `P07.ResolutionWorkflow` console app.
2. Workflow graph (verified MAF graph API): `TriageExecutor` (agent node, reuses P06 triage) → `DiagnosticExecutor` (agent node) → approval via `RequestPort.Create<FixApprovalRequest, ApprovalDecision>("FixApproval")` → `ResolutionExecutor` (writes summary note via `ITicketStore`, sets `Resolved`). Critical-priority tickets route through `EscalationExecutor` before approval.
3. HITL run loop: `InProcessExecution.RunStreamingAsync` + `WatchStreamAsync`; on `RequestInfoEvent` print proposed fix + prompt `y/n` + free-text note; `SendResponseAsync(request.CreateResponse(decision))`.
4. Rejected approval → ticket stays `InProgress` + rejection note appended; workflow ends for that ticket.
5. Checkpoint: serialize mid-approval state, kill process, restart, restore, answer pending approval. (Checkpoint API per docs; if self-managed checkpointing is insufficient in-place, JSON-state POC is acceptable with durable extension deferred to P09.)
6. Scenario: batch of 3 seeded tickets (one Critical) processed end-to-end; one approval rejected.
7. Tests: pure decision logic (`ApprovalPolicy`: which tickets need escalation, note formatting), ticket mutation assertions via store after workflow run.

## Success criteria

- All 3 tickets end in correct state (2 Resolved, 1 InProgress + note).
- Trace shows the graph path per ticket, including escalation edge for Critical.
- Kill-and-resume checkpoint works: pending approval re-emitted after restart.
- Unit tests pass.

## Stretch

- Fan-out: diagnose 3 tickets concurrently (`AddFanOutEdge` / `AddFanInBarrierEdge`).
- Reject with follow-up: rejected ticket re-enters diagnosis with the operator note as new input.

## Resources

- HITL (C# exact API): https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop
- Workflows overview / graph API: https://learn.microsoft.com/en-us/agent-framework/workflows
- Checkpoints: https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints
- HITL sample: https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/HumanInTheLoop/HumanInTheLoopBasic