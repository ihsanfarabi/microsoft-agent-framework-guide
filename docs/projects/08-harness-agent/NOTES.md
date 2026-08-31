# P08 NOTES — HarnessAgent

## What `AsHarnessAgent` wired vs P05–P07 manual wiring

- `client.AsHarnessAgent(new HarnessAgentOptions { ... })` (in sibling package
  `Microsoft.Agents.AI.Harness`, extension class `ChatClientHarnessExtensions`)
  returns a `HarnessAgent` that arrives with everything P02–P07 built by hand:
  - **Chat history persists to the session per model call, inside the tool
    loop** — the constraint the plan flagged as the whole reason to reuse one
    session. P02/P06 wired `UseFunctionInvocation` around a bare client and
    trusted the wrapper's loop; P03 built session persistence itself; here the
    harness does it, and the per-tool-call checkpoints in Program.cs land on
    completed model turns *holding real progress* (the kill evidence: the
    mid-flight snapshot already contained 23 chat-history messages).
  - **Resume hazard on those mid-run checkpoints**: the checkpoint fires when
    a `FunctionCallContent` *streams out* — potentially BEFORE the tool
    executes — so a resumed snapshot can contain an issued-but-unexecuted
    call whose result never persisted. Acceptable: the ticket store is the
    truth of what actually happened, and a close re-issued from the stale
    history is re-gated by the approval gate before it can act.
  - **Todo state lives in the session** — the model tracks its own backlog
    via built-in todo tools (5–6 todo items across runs on the 5-ticket
    batch); the killed session's snapshot showed todo 1 complete and todo 2
    still mid-flight, and the resumed run's first act was finishing todo 2.
    No custom state plumbing, unlike P07's
    `QueueStateUpdateAsync`/`ReadStateAsync` scope.
  - **File memory** — the default `FileMemoryProvider` roots at
    `agent-file-memory/<session-id>/` relative to the CWD; the session state
    carries the `workingFolder` link, so a restored session re-links to the
    *same* folder by session id (verified: the resumed run wrote its summary
    into the killed run's folder; no new folder created).
  - **Approval state lives in the session** — a standing "always approve this
    tool" rule (`CreateAlwaysApproveToolResponse`) is persisted by the
    harness's `ToolApprovalAgent` as a `ToolApprovalRule` in session state, so
    it survives the restart too. The gate itself is harness-supplied too —
    P05 intercepted its risky tool calls with hand-written
    `ToolApprovalMiddleware`; P08 just wraps `close_ticket` in
    `ApprovalRequiredAIFunction` and the harness's `ToolApprovalAgent` does
    the intercepting.
- The inverse lesson: the harness wires **its own function-invocation layer**
  innermost around the client (plus an approval-response-binding layer — the
  console log names `ApprovalResponseBindingChatClient`). Wrapping the client
  in `UseFunctionInvocation` first, mandatory since P02, is here *redundant*
  and dropped: plain `OllamaChat.Create()` goes into `AsHarnessAgent`.
- What the harness does NOT take over: mode selection defaults against you
  (below), file-access tools are approval-gated by default, and driving the
  run is still the caller's job.

## API gotchas found (Microsoft.Agents.AI.Harness 1.19.0, 2026-08-31)

- **The harness API ships in a sibling package.** `AsHarnessAgent` /
  `HarnessAgentOptions` do not exist in `Microsoft.Agents.AI` 1.19.0 at all
  (zero "Harness" hits across its XML/DLL strings) — the brief's `using`
  namespaces only resolve once `Microsoft.Agents.AI.Harness` 1.19.0 is
  referenced. `FileSystemAgentFileStore`, by contrast, lives in
  Microsoft.Agents.AI proper.
- **Default mode is "plan", and that silently ends the run.**
  `AgentModeProviderOptions` defaults to plan/execute with "plan" first; in
  plan mode the model narrates a plan, calls no tools, and the run finishes
  after one turn with the backlog untouched — exit 0, no error. An unattended
  batch needs `AgentModeProviderOptions = { DefaultMode = "execute" }`.
- **`UseFunctionInvocation` is caller-forbidden, not caller-owned** (above) —
  the exact inverse of P02–P06, where forgetting the wrapper meant no tools
  ever ran.
- **Drive with `RunStreamingAsync`.** Plain `RunAsync` returned after one
  turn during the broken runs; the documented canonical drive is
  `await foreach (var update in agent.RunStreamingAsync(prompt, session,
  cancellationToken: ct))` — which is also where the cancellation token goes
  (the P07-style kill path needs it).
- **The approval-rule signature in the blog does not exist.** Shipped
  `ToolApprovalAgentOptions.AutoApprovalRules` is
  `IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>` — the rule
  receives a context (`.FunctionCallContent`, `.Agent`, `.Session`), not a
  bare `FunctionCallContent`. Resolution here: keep the pure
  `ShouldAutoApprove(FunctionCallContent)` for the unit tests, add a context
  overload that delegates to it, and overload resolution makes the ruled
  wiring compile as written. Doc-drift rule (read the package XML first) held
  again; running ledger since P03 keeps growing.
- **Approvals surface as data, not a delegate**: the `ToolApprovalAgent`
  middleware *ends the agent run* yielding a `ToolApprovalRequestContent` in
  `AgentResponseUpdate.Contents`; the caller answers by sending a user
  `ChatMessage` carrying `request.CreateResponse(approved, reason)` (or
  `CreateAlwaysApproveToolResponse(reason)` for the standing rule) back on the
  same session. The loop wraps until a run completes with no pending request.
- **`ToolApprovalRequestContent.ToolCall` is typed `ToolCallContent`,** which
  in M.E.AI 10.9 only exposes `CallId` — but at runtime it carries the
  model's original `FunctionCallContent` (a derived type adding
  `Name`/`Arguments`). Downcast to print the wire name and args.
- **Session persistence round-trips `JsonElement`, not a string**:
  `ValueTask<JsonElement> SerializeSessionAsync(AgentSession, ...)` →
  `serialized.GetRawText()` → file, and `JsonDocument.Parse(file).RootElement`
  → `DeserializeSessionAsync`. The parsed `JsonDocument` must outlive the
  session: deserialized state-bag values may hold `JsonElement`s backed by it.
- `FileAccessStore`/`FileSystemAgentFileStore` are `[Experimental]` in 1.19.0
  — the MAAI001 analyzer escalates use to build errors; suppressed with a
  `#pragma warning disable MAAI001` scoped to exactly the construction site in
  `HarnessFacts.Build`.
- `FileTicketStore` takes a JSON *file* path, not a directory (the plan's
  `new FileTicketStore("work")` would have created a file named `work`).

## Approvals: what actually gated

- **Wrapping is the gate**: `new ApprovalRequiredAIFunction(
  AIFunctionFactory.Create(...))` (ctor shape `ApprovalRequiredAIFunction(
  AIFunction innerFunction)` — verified) on `close_ticket` in
  `TicketTools.All`. Without the wrap, no rule is ever consulted for the
  read-only ticket tools — at runtime `ApprovalPolicy.ShouldAutoApprove` is
  only exercised on `close_ticket`, where it returns false and falls through
  to the human prompt. `false` means "next rule / human decides", not reject.
- **`AutoApprovalRules` combines with OR semantics** — any rule returning true
  auto-approves; all false → human prompt. Observed with the custom ticket
  rule and `FileAccessProvider.ReadOnlyToolsAutoApprovalRule` in the same list.
- **File-access tools are `ApprovalRequiredAIFunction` by default** — an
  unattended run stalls on the first `file_access_*` call with nobody to
  answer (the model's other calls in the same turn are never invoked either;
  a spy client showed 27 tools sent, 4 calls emitted, zero invoked).
  `FileAccessProvider.ReadOnlyToolsAutoApprovalRule` auto-approves exactly
  read/ls/grep while keeping write/delete/replace gated.
- **Standing approvals are one answer, in session state**: `y` approves
  exactly this call — the very next close prompts again (single-approval
  semantics confirmed live); `a` records the standing rule, after which the
  remaining closes auto-pass (exactly 1 prompt for 5 closes in the final
  run) and it survives the restart with the session.
- **The instruction had to tell the model to CALL `close_ticket`.** With
  "request approval to close it", the model 2/2 narrated "requested approval"
  inside its resolution notes and never called the tool — the gate had
  nothing to fire on, and the batch completed without closing. Reworded to
  "call close_ticket to request approval to close it" the gate fired every
  run. An approval gate only exists at the rate the model actually invokes
  the gated tool.

## Kill and resume (three layers, because a kill has no exit path)

- **Continuous checkpoints**: serialize to `work/session-state/session.json`
  on every streaming update carrying a `FunctionCallContent` (once per issued
  tool call), plus on run-complete and on interrupt. Atomic write —
  `session.json.tmp` then `File.Move(overwrite: true)` — so a kill mid-write
  cannot tear the file.
- **Cooperative shutdown**: `Console.CancelKeyPress` (interactive Ctrl+C) and
  `PosixSignalRegistration.Create(PosixSignal.SIGTERM, ...)` both cancel a
  `CancellationTokenSource` wired into `RunStreamingAsync`; the enumeration
  unwinds via `OperationCanceledException` and an interrupt checkpoint runs.
  Two signal findings, each verified with a minimal repro before trusting:
  - **A detached run cannot be stopped with SIGINT at all** — backgrounded
    from a non-interactive shell it inherits SIGINT as `SIG_IGN`, .NET honors
    the inherited disposition, and `kill -INT` is silently a no-op (observed:
    the batch ran to completion). `kill -TERM` is the path.
  - **The SIGTERM callback must set `context.Cancel = true`** — without it the
    runtime performs default termination the moment the callback returns,
    and the wind-down never runs.
- **Restart**: `DeserializeSessionAsync` rehydrates the session id, todos,
  history, mode, approval state and file-memory link; the resumed run
  verified the two finished tickets read-only and did not redo them (still
  exactly 1 note each, no duplicate closes), then completed 5/5.
- **Corrupt state bricks loudly, never silently.** A truncated
  `session.json` moves aside to `session.json.corrupt-<ts>` (named, preserved,
  structurally impossible for the next save to overwrite), a `[fatal]` block
  names the exception and the remediation, and the process exits 1 — it does
  NOT start a fresh session, because a fresh agent would lose the todo list
  and history and could redo finished tickets (the no-redo constraint).
  Edge found while probing: a valid-but-empty `{}` deserializes fine as an
  empty session — the ticket store itself then guards no-redo.
- **Checkpoints fail soft**: any non-cancellation checkpoint failure is
  logged and skipped at every call site (a read-only `session-state/` dir
  still completed the batch 5/5); cancellation is re-thrown so the interrupt
  still unwinds.
- **The default file-memory root is CWD-relative** — a restart launched from
  a different directory re-links nothing; run the demo from the same CWD.

## Deviations from spec worth recording

- **"Reuse P02 ticket tools" was impossible as specified.** P02's surface
  has no `get_ticket`, no `close_ticket`, and no explicit wire names
  (`AIFunctionFactory` derives them from the `*Async` methods). P08 built a
  P08-local `TicketTools` with pinned snake_case names (P06 convention):
  `create_ticket`, `list_tickets`, `get_ticket`, `add_note`, `close_ticket`,
  with in-band error strings instead of throws so the model can recover
  mid-batch.
- `Microsoft.Agents.AI` pinned 1.19.0 (now stable), not `--prerelease` — the
  brief's flag predates stabilization; a prerelease add today installs a 1.20
  beta and breaks the repo's uniform pinning.
- The plan's "serialize on exit" couldn't survive a real kill (no exit path)
  — hence the continuous-checkpoint design above.
- **Spec requirement 6 is only half-met.** "Note formatting pure functions
  xUnit-tested" materialized for the approval policy (`ApprovalPolicy` is the
  tested pure function) but not for note formatting: it lives inline in
  `TicketTools`, not extracted for testing.

## Failure modes observed

- Mode-default narration (one turn, exit 0, backlog untouched) — looks like
  success to a script checking the exit code.
- Approval stall with no error: the run just never makes a second request.
- Model variance: the model sometimes batches all 5 closes in one turn; the
  one-at-a-time `??=` approval capture in Program.cs used to tangle when
  several gated calls queued in a single turn (the model itself reported
  swapped result messages, 3 tickets left open). Fixed: DriveAsync now
  captures *all* `ToolApprovalRequestContent`s per run and the loop answers
  each — one approval-response content per call id in a single user message.
- `tickets.json` sits inside the file-access root, so the model can read the
  raw store JSON via `file_access_read` (writes stay gated). Harmless here,
  worth remembering when the store holds anything sensitive.
- `AgentFileSkillsSource` logs "Discovered 0 potential skills" at startup —
  noise, not a bug.

## Stretch not done

- Plain `ChatClientAgent` + only `FileMemoryProvider` as context provider
  (observe what breaks without the harness) — skipped: it needs a fresh live
  batch against Ollama, and the controller ruled the runtime evidence
  complete without another run. The predictable shape, from the blocker
  evidence above: plan-mode narration gone but approvals, todos and
  file-access gone too — every fix in this project was a harness default
  turned *up* or *down*, so the interesting question is which defaults a bare
  agent leaves missing entirely.
