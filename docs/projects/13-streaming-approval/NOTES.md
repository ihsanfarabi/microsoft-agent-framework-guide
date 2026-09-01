# P13 — StreamingApproval notes

Streaming chat + mid-stream tool approval over SSE from a self-hosted ASP.NET
Core endpoint: the run pauses at the first gated tool call, an `event: approval`
frame carries `{requestId, tool, args}`, a second HTTP POST (`/approvals/{id}`)
answers it, and the stored session resumes as a new SSE stream. P08's
in-process `DriveAsync`/`PromptApproval` loop, HTTP-ified.

## What worked

- **The agent-level approval round trip works over streaming**:
  `ChatClientBuilder(...).UseFunctionInvocation()` (mandatory — it runs the
  tool loop AND is the layer that turns a gated call into a
  `ToolApprovalRequestContent`) wrapped by
  `new AIAgentBuilder(inner).UseToolApproval(new ToolApprovalAgentOptions {
  AutoApprovalRules = [...] })`. A run that hits a gated call ends by yielding
  the request in `update.Contents`; resume is
  `RunStreamingAsync(new ChatMessage(ChatRole.User, [request.CreateResponse(approved, reason)]), sameSession)`
  — P08's shape verbatim. `CreateAlwaysApproveToolResponse` for the standing
  rule, and the always-approve entry persists in session state (survives
  restarts with the checkpointed session).
- **Pure SSE framing**: `SseWriter.FramesFor/EnumerateFrames/WriteAsync`
  map streaming updates to frames with no HTTP state, so the whole pipeline
  (function invocation + approval middleware + framing) runs offline over a
  scripted `IChatClient` — the approval contract is tested without Kestrel,
  and the endpoint itself via `WebApplicationFactory`.
- **Per-conversation session checkpoints** (P08's pattern): serialize after
  every stream end with an atomic temp-file move, rehydrate on demand, and
  *verify the round trip* by re-serializing a restored session before trusting
  it. A checkpointed conversation restores its history in a fresh process.
  Unlike P08's brick-on-corrupt, fail-soft to a fresh session is safe here:
  every destructive call is approval-gated again, so a lost history loses
  memory, never a decision.
- **The client loop is the demo**: MAF 1.19 surfaces one approval per pause
  round, so `scripts/demo13.sh` streams turn 1, parses the `event: approval`
  frame, POSTs the vote, and repeats until no approval frame remains — the
  same loop a real SSE client must implement, with no server-side recursion
  (a resumed turn that gates again just ends in a fresh approval frame).
- **The tombstoning `DeletableTicketStore`**: `ITicketStore` has no delete
  verb and Core is frozen, so deletes tombstone into a side file and hide
  from reads — the demo's end-to-end proof (the tool really ran only after
  the vote) is directly assertable off the tombstone file.

## Doc-vs-reality divergences

- **`MapOpenAIChatCompletions` silently drops approval content — the reason
  this project has a custom endpoint.** The OpenAI-compatible hosting layer
  maps agent content to Chat Completions deltas with a switch that handles
  `TextContent`/`FunctionCallContent` and ends `_ => null`; a
  `ToolApprovalRequestContent` produces no SSE frame at all ("unsupported but
  expected content type"), and there is no inbound channel to carry a response
  back even if it were surfaced. See
  [`AIAgentChatCompletionsProcessor.cs`](https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Hosting.OpenAI/ChatCompletions/AIAgentChatCompletionsProcessor.cs)
  (`dotnet/src/Microsoft.Agents.AI.Hosting.OpenAI/ChatCompletions/`): a
  request that needs human approval just looks like a run that stopped early.
- **`ToolApprovalRequestContent` and `ApprovalRequiredAIFunction` live in
  `Microsoft.Extensions.AI`** (MEAI 10.9 abstractions), not
  `Microsoft.Agents.AI` — the docs read as MAF types. The inverse for the
  response side: `CreateAlwaysApproveToolResponse` is a MAF extension
  (`ToolApprovalRequestContentExtensions`), while `CreateResponse` is MEAI.
- **`UseToolApproval` extends `AIAgentBuilder`,** not a `ChatClientAgent`
  option — the built pipeline is a `ToolApprovalAgent` wrapping the
  `ChatClientAgent`, and `Build` returns `AIAgent`. All run/stream/serialize
  APIs are on the `AIAgent` base, so nothing downstream changes. (Unlike
  P08's harness file-access types, it is not `[Experimental]` in 1.19.0 — no
  MAAI001 suppression needed.)
- **One pause per run — the burst story differs from the docs/P08 account.**
  The P13 spec (following P08, where the model burst several gated calls and
  `DriveAsync` answered all surfaced requests at once) expected multi-request
  bursts. Under this stack (`ChatClientAgent` + `UseFunctionInvocation` +
  agent-level `UseToolApproval`), a turn with two gated calls ends the run at
  the FIRST one: one surfaced request per pause, the next on the resumed run.
  The burst contract is therefore client-side: post → read the new approval
  frame → post again, in order. `SseWriter`/`PendingApprovals` still handle
  the true multi-request update shape (two requests in one update park and
  frame as two events) — the middleware just never surfaces it in one go.
- **A continuation that does not answer the pending request fails before the
  next model call.** Posting a normal message on a paused conversation throws
  `InvalidOperationException: ToolApprovalRequestContent found with
  FunctionCall.CallId(s) ... that have no matching ToolApprovalResponseContent`
  — the session history carries the unpaired request, so the harness refuses
  the stimulus. Surfaced as an `event: error` frame (never a 500).
- **Re-park is best-effort, not a rewind.** On a failed/cancelled resume the
  taken approval is put back so the operator can answer again — but the gated
  call may already have EXECUTED (it runs before the model call that failed).
  A re-answer then finds no matching pending call and surfaces an error:
  no double execution, but also no undo. The store is the truth of what ran.
- **Restart-with-pending is a documented dead end, not a resume.** Pending
  approvals are in-memory while sessions checkpoint to disk, so after a
  restart the conversation history survives but the parked turn died with the
  process: the vote gets an `unknown-request-id` error frame, and a new
  message on that conversation gets the `approval-required` frame pointing to
  a new conversation (the checkpointed session still holds the parked
  request, and there is no "cancel approval" verb to clear it).

## What to do differently next time

- **The in-memory pending-approval store is the deliberate tradeoff** (SPEC
  non-goal): a `PendingApprovals` row is a live `ToolApprovalRequestContent`
  plus the open `AgentSession`, both process-bound. Making them durable means
  serializing both across processes — worth it only once a real operator
  actually walks away mid-approval; until then the error-frame recovery ("ask
  again") is honest and simpler.
- **SSE through a `StreamWriter` fails on the test server** ("Synchronous
  operations are disallowed" — `AutoFlush` performs a synchronous flush).
  Write UTF-8 bytes straight to `Response.Body` with async writes; every
  future SSE endpoint here should copy that shape.
- **Fixed conversation ids in tests become stateful once sessions hit disk** —
  the T2 tests tripped over parked approvals from a previous run's
  conversation ids (which is itself proof the persistence works). Conversation
  ids are now unique per test run; demo scripts should do the same.
- **The seed creates a fresh GUID per restart** once the previous demo ticket
  is tombstoned (`ListAsync` hides tombstones, so seeding re-creates), and
  `FileTicketStore` keys are GUIDs, not T-numbers — the demo script reads the
  live ticket id out of the store files rather than quoting a constant.
- **Port 5000 is not free on macOS** — the AirPlay receiver holds it, so the
  demo script binds an explicit port via `ASPNETCORE_URLS` instead of relying
  on Kestrel's default.
- The approval `event:` payload's `args` echoes the model's raw arguments —
  fine for a demo, but a production surface would want the arguments
  re-validated server-side before execution (the vote consumes the request;
  nothing re-checks that `args.id` still names a live ticket).
