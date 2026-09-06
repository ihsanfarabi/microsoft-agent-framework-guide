# P07 NOTES — ResolutionWorkflow

## Graph vs orchestration (vs P06 handoff)

- P06's handoff builder auto-injects the routing *into the model*: agents
  decide who holds the task by calling a handoff tool. P07's `WorkflowBuilder`
  puts routing in *code*: `AddEdge` (+ `condition:` lambdas) express the
  ticket-resolution pipeline — Triage → Diagnose → (Escalation if Critical) →
  Approval — so the control flow is explicit, inspectable, and versionable.
  The LLM still decides *within* nodes (classification, diagnosis), but never
  whether a step happens.
- Conditional routing is done by edge condition on the same `TicketContext`
  type (`condition: (TicketContext t) => ...`), which is why one shared
  message record across the graph works: `AddEdge<T>`'s type parameter is
  inferred from the typed lambda.
- One `record TicketContext(...)` shared by all executors is exactly the right
  shape: every node refines it with `with` — the graph state is visible data,
  not hidden conversation turns.

## API gotchas found (Microsoft.Agents.AI.Workflows 1.19.0, 2026-08-30)

- **Executor ids must be unique across the whole graph** — including the
  `RequestPort`'s id. Naming the approval executor "FixApproval" (same as the
  port) dies at build: *"Cannot bind executor with ID 'FixApproval' because an
  executor with the same ID but a different type (ApprovalExecutor vs
  RequestInfoExecutor) is already bound."*
- **HITL response routing is edge-based, not magic**: the RequestPort's
  response is delivered along the *reverse edge* `AddEdge(port, executor)`.
  Without it the runtime emits the `RequestInfoEvent`, accepts the response,
  and the workflow silently completes with the answer consumed and dropped —
  no error. The doc's "routes back to the executor that sent the request"
  holds, but only because that edge exists.
- **One executor, two message types** (send the request *and* receive the
  response) needs the non-generic `Executor` + imperative `ConfigureProtocol`:
  `protocol.ConfigureRoutes(routes => routes.AddHandler<TicketContext>(...)
  .AddHandler<ApprovalDecision>(...))`. The `[MessageHandler]`/`partial`
  source-generating pattern in the current docs has **no generator shipped in
  1.19.0** — `Executor.ConfigureProtocol` stays abstract and the build fails
  with CS0534. `Executor<T>` is sugar over the same mechanism.
- **A `Workflow` instance is owned by its first runner.** Running it twice —
  even sequentially, even via `ResumeStreamingAsync` — throws *"Cannot use a
  Workflow that is already owned by another runner or parent workflow."*
  Rebuild the graph per run (identical topology + executor ids is exactly
  what checkpoint rehydrate requires anyway).
- Condition lambdas take `T?` (`Func<TicketContext?, bool>`): write
  `(TicketContext t) => ...` with `t!` inside, or the compiler warns.
- `ExternalRequest` exposes `TryGetDataAs<T>(out T)` (not `GetDataAs<T>`),
  mirroring the sample's pattern-matching style.

## Where HITL state lives

- The pause point is the `RequestPort`: the approval executor sends
  `FixApprovalRequest` along the edge to the port; the port surfaces
  `RequestInfoEvent`; the host answers with `Request.CreateResponse(...)` +
  `handle.SendResponseAsync(...)`; the runtime delivers the typed
  `ApprovalDecision` to the same executor that asked.
- The *ticket context* survives the pause in workflow shared state
  (`QueueStateUpdateAsync` / `ReadStateAsync<T>` with a scope). Executor
  fields would leak across the batch's sequential runs and don't checkpoint;
  shared state is the thing that survives restore.
- Store mutations (store status + note) are deliberately the *only* side
  effect of the decision handler — "observable outcomes" stay outside the
  workflow engine, so a killed process leaves at most a pending approval and
  never a half-written ticket.

## Checkpoint mechanism used

- `CheckpointManager.CreateJson(new FileSystemJsonCheckpointStore(dir))` —
  JSON checkpoints on disk (durable across processes), passed as the third
  arg to `InProcessExecution.RunStreamingAsync`. `CheckpointManager.Default`
  is in-memory only — useless for kill-and-resume.
- Checkpoints are created automatically at the end of each *super step*;
  capture `CheckpointInfo` (SessionId + CheckpointId) from
  `SuperStepCompletedEvent.CompletionInfo.Checkpoint` and persist it
  (`p07-checkpoint-info.json`). Two checkpoints per HITL cycle: one right
  after the `RequestInfoEvent` goes out (the resume point), one after the
  answer (see the MAF doc's super-step accounting).
- Resume across process restart:
  `InProcessExecution.ResumeStreamingAsync(newWorkflow, checkpoint,
  checkpointManager)`. Pending requests saved in the checkpoint are
  **re-emitted as `RequestInfoEvent`** on restore — the host's answer loop
  needs no changes for the resume path (verified: killed mid-approval on
  ticket 2, restart, answered, workflow completed; tickets 1–2 Resolved,
  ticket 3 then ran and ended InProgress).
- Failure modes: the checkpoint is *eventual* (end of the super step that
  emitted the request — anything after the prompt is lost by design); the
  `FileSystemJsonCheckpointStore` is documented as process-exclusive —
  fine for a demo, would need a real store per host in production;
  `InMemoryTicketStore` would have lied about durability, so the demo store
  is the JSON-file `FileTicketStore`. True durable execution (activity
  retries, timers, cross-host resume) is deferred to P09.

## Failure modes observed

- An unhandled executor exception surfaces as `ExecutorFailedEvent` /
  `WorkflowErrorEvent` in the stream rather than throwing from the host loop —
  catch both in the demo loop or a bad model reply ends the run silently.
- Specialist choice runs on LLM output ("network" vs "Network.") — the
  fallback in `Agents.SpecialistFor` (anything unrecognized → network
  specialist) keeps the graph deterministic; a stricter version would use
  structured output from the classifier.

## Stretch ideas not done

- Fan-out diagnosis of all 3 tickets concurrently (`AddFanOutEdge`) —
  ApprovalExecutor's shared-state "pending ticket" slot is single, so fan-out
  would need per-ticket keys (`ctx.TicketId`) for state.
- Reject-with-follow-up re-entering diagnosis: the operator note is already
  on the decision; the re-entry edge is what's missing.