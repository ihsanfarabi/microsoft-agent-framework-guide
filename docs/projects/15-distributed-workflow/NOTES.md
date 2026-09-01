# P15 — DistributedWorkflow notes

One local `WorkflowBuilder` graph whose middle nodes are remote A2A agents:
`TriageExecutor` (local) → `DiagnosisAgent` (P15 service, :5200) → conditional
edge → `InventoryAgent` (P09 service, :5199) → `ReportExecutor` (local). The
conditional edge skips the inventory hop on software-only tickets — the route
is decided by the graph, not the LLM. Two processes per remote hop, three
processes in one trace. The failure demo (`scripts/demo15-failure.sh`) kills
the inventory service and proves the dead hop dies *inside* the workflow,
visibly, as a `WorkflowErrorEvent` — not at startup.

## The by-contract vs by-example gap: the plan snippet could never route

The plan's graph snippet was written from research notes and reads like
reasonable 1.19.0 — and every edge in it is dead on arrival:

```csharp
// from the plan — none of these conditions can ever fire
.AddEdge<AgentResponse>(diagnosis, inventory, r => ContainsHardware(r))
```

- **`AgentResponse` is not the agent node's edge payload.** Decompiled
  `AIAgentHostExecutor` / `ChatProtocolExecutor` (Microsoft.Agents.AI.Workflows
  1.19.0): an agent node sends its turn's response as `List<ChatMessage>` via
  `SendMessageAsync` — `AgentResponse` exists only as an *output event*
  (`EmitAgentResponseEvents = true`), never as an in-process edge message. A
  condition typed on `AgentResponse` matches nothing, ever, silently — the
  edge exists, the graph runs, the message just never takes it. This is the
  sharpest by-contract vs by-example gap in the curriculum so far because the
  failure mode is silence, not a compile error.
- **`InProcessExecution.RunAsync(workflow, input)` DOES exist in 1.19.0** —
  the initial T2 claim that it doesn't was wrong (a reviewer compile-tested a
  scratch project against the real package: it returns `ValueTask<Run>`). Both
  the report and a Program.cs comment originally repeated the false claim.
  Lesson recorded twice over: an API-existence claim made from reading
  decompiled sources is still a guess until something compiles against the
  real package — and a claim written into a comment outlives the correction.
  The orchestrator still uses `RunStreamingAsync` + `WatchStreamAsync`
  deliberately, for the P07-style event loop (and because streaming is where
  `WorkflowErrorEvent` surfaces — see below).

The plan hedged correctly, though: "(Exact input/output types resolved
against `ChatProtocol` at implementation)" — the escape hatch that made this a
fix, not a redesign. The SPEC's wrapper-Executor fallback was never needed.

## ChatProtocol boundary: the payload types that broke, the shape that worked

The exact types at the agent-node boundary, all decompiled from 1.19.0 and
verified live:

- **In**: `string` (wrapped as `ChatRole.User` by the host's default options),
  `ChatMessage`, `IEnumerable<ChatMessage>`/array, and `TurnToken`. Messages
  **accumulate**; the hosted agent is invoked only when a `TurnToken` arrives.
  A bare string from `TriageExecutor` would have sat there forever — the
  handshake is mandatory, which is why Triage sends `ChatMessage` +
  `TurnToken`, and why the `HardwareGateExecutor` node exists (the review
  round moved the TurnToken send out of a side-effecting edge condition into
  that gate node).
- **Out**: `List<ChatMessage>` — the turn's responses — **plus** a trailing
  fresh `TurnToken` (`ContinueTurnAsync` sends one downstream after every
  turn). With `ForwardIncomingMessages = true` (the default) the node also
  forwards the messages it *received* (reassigned to the user role via
  `ReassignOtherAgentsAsUsers`) ahead of its response: each agent node emits
  [forwarded user-role list] → [response list] → [TurnToken].
- **Edge conditions receive the raw envelope message** (`DirectEdgeRunner.
  ChaseEdgeAsync`), and after a condition passes a `CanHandle(target,
  runtimeType)` gate silently drops messages the target can't handle
  (`DroppedTypeMismatch`). That gate is load-bearing, not an error path: it
  is how stray `TurnToken`s die harmlessly on edges into plain executors —
  `ReportExecutor` registers only `AddHandler<List<ChatMessage>>` and the
  trailing tokens vanish without a `WorkflowErrorEvent` (verified live).
- **The shape that worked — conditions typed on the real edge payload:**

```csharp
.AddEdge<List<ChatMessage>>(diagnosisNode, hardwareGate, NeedsHardware)
.AddEdge<List<ChatMessage>>(diagnosisNode, report, SoftwareOnly)
```

  with the predicates requiring an **assistant-authored** message
  (`Role == ChatRole.Assistant`) before matching `NEEDS-HARDWARE`. Two
  reasons that last bit matters: (1) forwarded inbound chatter arrives as
  user-role lists (A2A `Role.Agent` → `ChatRole.Assistant` in the client
  package's `AIContentExtensions`, but *forwarded* input is reassigned to
  user), so only the real result list contains an assistant message; (2) a
  ticket that merely echoes "NEEDS-HARDWARE" must not be able to route the
  graph (review fix). Edge conditions are also pure predicates — no prints,
  no closure mutation; the run's route summary is derived after the run from
  which `WorkflowOutputEvent`s (their `ExecutorId`) actually appeared.
- **The wire is entirely SDK-owned**: `A2AAgent` flattens the message list
  into one `Message { Role = Role.Agent }` (`ChatMessageExtensions.
  ToA2AMessage`) — no hand-crafted JSON anywhere in the orchestrator, which
  is exactly why the server-side wire drift below never touches it.

## A2A wire drift: `role` is a numeric enum on the wire

`A2A.AspNetCore 1.0.0-preview2` rejects `{"role":"user"}` with
`BadHttpRequestException: The JSON value could not be converted to A2A.Role`
— the wire enum is numeric-only: `role: 0` = user (responses come back as
`"role":"ROLE_AGENT"`, a string, asymmetrically). Cost a curl round-trip to
find; costs nothing in the orchestrator because it uses SDK types end to end
(`A2ACardResolver.GetAIAgentAsync()` + `RunAsync`). But raw-curl A2A probes
(this repo's standard verification tool) must send `role: 0`, and nothing in
the error message hints at it.

## Discovery: the honest down-service fallback

`DiscoverAsync` deliberately does **not** abort when an agent card is
unreachable. Aborting at discovery would move the failure to startup, before
any workflow exists — precisely the "failure invisible" behavior P15's task 3
exists to kill. The fallback binds the *configured* endpoint by hand
(`AgentCard` → `AsAIAgent()`, HTTP+JSON binding, same path the service maps)
and lets the first real A2A call produce the genuine connection error at the
hop, where the streaming event loop can surface it. The transcript says so:
`[discovery failed] …` then `[discovery fallback] binding configured endpoint
… anyway — the failure will surface at the hop, inside the workflow`.

- **The resolver wraps, not throws, transport errors** — the detail that
  almost broke the fallback: decompiled `A2ACardResolver.GetAgentCardAsync`
  (a2a 1.0.0-preview2) surfaces connection failures as
  `A2AException("HTTP request failed", HttpRequestException)` and card-parse
  failures as `A2AException("Failed to parse JSON: …")` with a **null**
  InnerException — the resolver uses the single-arg ctor, interpolating the
  JsonException's message into the wrapper text (decompile-verified; no
  inner is attached). A
  catch written for a bare `HttpRequestException` (the obvious shape) matches
  *nothing* — the fallback would never fire. The filter is
  `ex is HttpRequestException || ex is A2AException { InnerException:
  HttpRequestException }` — transport failures only. A live-but-sick service
  serving a malformed card (null inner — not an HttpRequestException) and
  `OperationCanceledException` propagate untouched: the fallback absorbs
  "service is down", never "service is lying".
- The fallback is not a swallow — it *relocates* the failure from "startup,
  before any workflow exists" to "the hop, as a `WorkflowErrorEvent`", the
  only place the event loop can see it.

## Failure visibility: propagate + WorkflowErrorEvent (handle-or-propagate = PROPAGATE)

- A remote hop dying mid-run arrives in the streaming loop as an
  `ExecutorFailedEvent` (which executor, raw exception) and then a
  `WorkflowErrorEvent` (the run-level error). Both are printed verbatim —
  nothing suppressed.
- After the stream ends, the orchestrator derives **which hop died** from the
  executor outputs actually seen and throws a `WorkflowFailedException`
  wrapping the original exception untouched (`InnerException` preserved,
  message quoted verbatim): the wrapper adds the one thing a socket error may
  not make obvious — the A2A endpoint being called
  (`http://localhost:5199/a2a/inventory`). The top-level handler prints both
  and exits 1.
- **Why no retry:** the orchestrator is a stateless console host; a dead
  remote hop is not a transient fault it can classify, and retry policy
  (attempts, backoff, idempotency — the P09 reservation side effect makes
  blind retries unsafe) belongs to a durable workflow host. **Deferred to
  P16** — durability was explicitly out of scope for P15 (see SPEC), and
  retries are a durable-host concern, not an orchestrator concern.
- **Why no continue-to-next-scenario:** continuing after a failure is exactly
  the "failure invisible" behavior this task kills. The demo script therefore
  runs the failing and succeeding scenarios as separate process invocations
  (`dotnet run -- B` exits 1; `dotnet run -- A` runs fresh and exits 0) —
  and the script *refuses to run* if :5199 is already listening, because a
  live inventory service would make the "failure" demo dishonest.
- The contrast run is the curriculum point: with the same graph and the same
  dead :5199, the software-only scenario exits 0 — the conditional edge
  dropped the result and its `TurnToken` before the inventory node ever ran,
  so zero A2A calls hit the dead endpoint. The failure belongs to the hop
  that was actually called; the routing decides which hops are on the path.

## Trace / evidence pointers

- Two `A2AClient/SendMessage` spans (one per target) in a hardware run;
  exactly one (:5200 only) in a software run — the per-target URL tags are
  the "two remote A2A targets in one workflow" evidence. Console-exporter
  transcripts (`P15_TRACE_CONSOLE=1`) are the recorded trace record; the
  compose Aspire dashboard publishes no OTLP ingestion port (P09 note).
- `scripts/demo15-failure.sh` asserts on captured output only: failure run
  exits non-zero with a `[workflow error]`/`[FAILED]` line naming 5199
  (pinned to the error path — the `[discovery failed]` line also says 5199
  and does not count); software run exits 0 with the `SKIPPED` route summary.
