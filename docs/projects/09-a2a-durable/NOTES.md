# P09 NOTES — A2A + Durable workflows

## A2A discovery flow (Task 2/3)

- The inventory service hosts the `InventoryAgent` under the A2A **HTTP+JSON
  binding**: `AddA2AServer("inventory")` requires a **keyed singleton**
  `AIAgent` registration (`AddKeyedSingleton<AIAgent>("inventory", ...)`),
  then `app.MapA2AHttpJson("inventory", "/a2a/inventory")` serves the message
  endpoint and `app.MapWellKnownAgentCard(card, ...)` publishes the discovery
  document.
- **Divergence from the plan**: the card is served at
  `/.well-known/agent-card.json`, not `/.well-known/agent.json` — the plan's
  citation 404s in this package set (A2A.AspNetCore 1.0.0-preview2).
  `A2ACardResolver` defaults to the same well-known path, so the client only
  needs the host base URI: `new A2ACardResolver(new Uri("http://localhost:5199"))`
  — the plan's full-card-endpoint URL in the resolver constructor is neither
  needed nor matches the default path.
- The discovery step is literally one awaited call: `AIAgent remote = await
  resolver.GetAIAgentAsync();` — no manual `AgentCard` field reading. The
  client then treats the remote agent as an ordinary `AIAgent`.

## Remote agent as a tool vs P06's in-process agent-as-tool

- P06 composed specialists in-process: agent instances handed straight into
  other agents' tool lists. P09's helpdesk client instead wraps the remote
  agent with `.AsAIFunction(name: "check_laptop_stock")` — the same shape P06
  built, but every call now crosses HTTP as an A2A message.
- What changes in practice: the tool loop runs in the helpdesk agent's
  process; each tool-call round-trips to port 5199 as an A2A
  `message/send`, so the remote side's tools (check stock / reserve) stay
  invisible to the client and live only behind the card's declared skills.
- **Divergence**: `.AsAIFunction()` lives in the `Microsoft.Agents.AI` core
  package, not in the A2A client package — the plan's package list for the
  client was short one explicit `Microsoft.Agents.AI 1.19.0` reference.
- InventoryAgent needed its catalog SKUs spelled out in instructions; the
  model otherwise cannot know that `LT-001` is the ThinkPad to reserve (the
  tool takes a SKU).
- Trace check (spans crossing the HTTP boundary in the Aspire dashboard) was
  config-verified only: the dashboard's API wanted auth and the anonymous
  restart was repeatedly blocked by session issues. Both services report OTLP
  to localhost:4317 with resource service names `P09.InventoryAgentService`
  and `P09.HelpDeskClient`.

## Durable workflow semantics (Task 4)

- The P07 graph runs unchanged **topologically** on Durable Task:
  `ConfigureDurableWorkflows(o => o.AddWorkflow(workflow))` registers it, and
  the orchestration appears as `dafx-ResolutionWorkflow`.
- Executors dispatch as individual **activities** named `dafx-<ExecutorId>`
  (`dafx-Triage`, `dafx-Diagnose`, `dafx-Approval`), each receiving a
  `DurableActivityInput` of `{ input, inputTypeName, sharedState }`. LLM calls
  run inside activities — replay-safe because orchestrations only re-execute
  the routing/replay code, never re-run completed activities.
- `RequestPort` dispatch is the HITL specialization: the orchestration
  publishes the pending request into its **custom status**
  (`DurableWorkflowLiveStatus.PendingEvents`), calls
  `WaitForExternalEvent<string>(portId)` and suspends — durable semantics, so
  killing the host process leaves the paused orchestration in the scheduler.
  Hosts re-attach by streaming the instance's custom status back as
  `DurableWorkflowWaitingForInputEvent`s.
- **The DTS emulator stands in for Azure Durable Task Scheduler**: the same
  `Microsoft.DurableTask.*AzureManaged` client/worker packages, pointed at
  `Endpoint=http://localhost:8080;TaskHub=default;Authentication=None`
  (container `mcr.microsoft.com/dts/dts-emulator:latest`, ports 8080/8082).
  Everything above the scheduler connection string is cloud-portable.
- Kill-and-resume observed end-to-end: fresh run → approve-prompt → answer
  `k` → host exits 137 → `dotnet run -- resume` re-attaches, the pending
  request is re-emitted **from the scheduler**, the answer routes to the
  Approval executor, and only `dafx-Approval` re-runs (triage/diagnose
  history is not re-executed). Ticket state lands in `p09-tickets.json`
  (Resolved + note).

## Runtime divergences from the plan's verified citations

Each of these was hit at runtime and decompiled to root cause
(ilspycmd over the 1.16.0-preview / 1.19.0 assemblies):

- **`.WithName("ResolutionWorkflow")` is mandatory for durable
  registration** — the analyzer rejects an unnamed workflow ("Workflow must
  have a valid Name property"). The in-process P07 run ignores it. The plan's
  P07 builder had no name.
- **Enum round-trips through DurableSerialization**: the preview host writes
  string enums on one hop and deserializes with numbers-only options on the
  next ("The JSON value could not be converted to TicketContext. Path:
  $.priority"). Fixed by a per-enum JSON converter on `TicketPriority`
  (writes numbers, reads both) in `MafDemo.Core`.
- **`YieldOutputAsync` crashes the durable host**: yielding caused first
  "Cannot output object of type String. Expecting one of []", then — with
  `YieldsOutputType` declared — an unhandled `KeyNotFoundException`
  (`GetProperty("sourceId")`) when `TryDeserializeEvent` replayed
  `WorkflowOutputEvent`s out of the custom status (property-name
  case mismatch; the catch only covers `JsonException`). Executors now print
  progress to the console instead of yielding outputs — activities run once
  per real execution, so the print never replays.
- **Durable conditional edges receive untyped messages for void executors**:
  `RouteOutputToSuccessors` prefers sent messages (typed) but evaluates edge
  conditions with `DeserializeForCondition(json, _sourceOutputType)`, and
  `GetExecutorOutputType` only extracts an output type from
  `Executor<TInput, TOutput>` — for P07's `Executor<TicketContext>` executors
  the router's type is null, so conditions received boxed `JsonElement`s
  (case-mismatched camelCase). Fix: declare the conditions as
  `Func<object?, bool>` and self-parse — accept either a live `TicketContext`
  (in-process) or a boxed `JsonElement` (durable), deserializing with
  `JsonSerializerDefaults.Web` (DurableSerialization is camelCase).
- **RequestPort responses arrive AS UNTYPED messages**:
  `SendResponseAsync` → `RaiseEventAsync` → `WaitForExternalEvent<string>`
  strips the type, and the port's result is routed with `typeName: null`;
  the executor then falls back to its **first registered handler**
  (`ResolveInputType`: null/unknown name → `supportedTypes.First()`). With
  `TicketContext` registered first, the ApprovalDecision deserialized as an
  empty `TicketContext` and OnTicket re-ran in an infinite approval loop.
  Fix: register `AddHandler<ApprovalDecision>` **before**
  `AddHandler<TicketContext>` (in-process dispatches on the live object's
  type, so registration order is irrelevant there). The same null-type path
  is also why the very first durable run's conditions saw nulls.
- **Resume is not exposed**: `IWorkflowClient` schedules new instances and
  awaits them, but has no API to re-attach a stream to an existing run, and
  `DurableStreamingWorkflowRun`'s constructor is internal. P09 re-attaches by
  reflection (`client.GetInstanceAsync(runId)` for the status check + a
  reflection-built `DurableStreamingWorkflowRun(client, runId, workflow)`);
  `WatchStreamAsync` is pure custom-status polling, so that handle is all a
  resume needs.
- **`DurableWorkflowWaitingForInputEvent.Input` is a raw JSON string** (the
  plan sketched a typed accessor); the host deserializes `FixApprovalRequest`
  from it with camelCase/Web options.
- **Stale-binary trap**: editing executor code then `dotnet run --no-build`
  ran old DLLs (misleading stack symbols). Full `dotnet build` before any
  `--no-build` run.
- Two DurableHost processes pointed at the same task hub silently steal each
  other's activity work items (workers compete for dispatch), which scrambles
  logs across consoles — the debugging artifact of the previous bullet.

## Task 1/2 smaller notes

- Inventory store: CAS `TryUpdate` on the `InMemoryInventoryStore` for
  reserve-decrement (no lock), seed via `Seed([...])` from Program.cs,
  reserved count is tracked on the item (`Available`/`Reserved`).
- The service console only logs requests once `appsettings.Development.json`
  raises `Microsoft.AspNetCore` to `Information` — launchSettings pins the
  Development environment, and that file overrides the base one.