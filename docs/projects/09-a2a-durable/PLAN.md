# P09: A2aDurable — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Inventory agent exposed over A2A from a separate ASP.NET Core service + P07 resolution workflow made durable (kill/restart resumes).

**Architecture:** Three projects: inventory service (A2A server), helpdesk client (A2A client over Ollama), durable host (generic host + Durable Task worker running P07 workflow against local scheduler emulator).

**Tech Stack:** .NET 10, `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`, `Microsoft.Agents.AI.A2A`, `Microsoft.Agents.AI.DurableTask` + `Microsoft.DurableTask.*.AzureManaged` (all prerelease), OllamaSharp, xUnit.

**Spec:** `docs/projects/09-a2a-durable/SPEC.md`

## Global Constraints

All API below verified against live MS Learn docs + NuGet on 2026-08-30. Latest package line is 1.19.0-preview (A2A) / 1.16.0-preview (DurableTask); `dotnet add --prerelease` resolves these.

- **A2A server** (verified: learn.microsoft.com/agent-framework/hosting/self-hosting/a2a/dotnet): packages `Microsoft.Agents.AI.Hosting.A2A.AspNetCore --prerelease` (pulls core) + `A2A.AspNetCore --prerelease`. NO Azure packages needed (doc sample uses Foundry; Ollama replaces it). Server pattern is NOT `AddAIAgent`/`MapA2A`:
  ```csharp
  builder.Services.AddKeyedSingleton<AIAgent>("inventory", (sp, _) => agent);
  builder.AddA2AServer("inventory");
  var app = builder.Build();
  app.MapA2AHttpJson("inventory", "/a2a/inventory");   // HTTP+JSON binding (MapA2AJsonRpc for JSON-RPC)
  app.MapWellKnownAgentCard(new AgentCard { Name = "InventoryAgent", ..., SupportedInterfaces = [...] }); // from A2A.AspNetCore
  ```
  Card served at host-root `/.well-known/agent.json` (server doc). Client doc claims `/.well-known/agent-card.json` — docs disagree; probe both at runtime.
- **A2A client** (verified: .../integrations/by-component/agent-services/a2a): package `Microsoft.Agents.AI.A2A --prerelease`. Discovery — resolver takes the remote HOST base URI, not the endpoint path (it fetches the card from the well-known path):
  ```csharp
  var resolver = new A2ACardResolver(new Uri("http://localhost:5199"));
  AIAgent remote = await resolver.GetAIAgentAsync();  // card + agent in one call
  ```
  Alternatives: `agentCard.AsAIAgent()`, or direct `new A2AClient(new Uri(...)).AsAIAgent(...)`.
- **Agent as tool** (verified: .../agents/tools "Using an Agent as a Function Tool"): extension is `.AsAIFunction()` on the AIAgent (NOT `AsAITool`): `tools: [remote.AsAIFunction()]`.
- **Durable BYO-compute** (verified: learn.microsoft.com/azure/durable-task/sdks/durable-agents-microsoft-agent-framework + devblogs durable-workflows): packages `Microsoft.Agents.AI.DurableTask --prerelease`, `Microsoft.DurableTask.Client.AzureManaged`, `Microsoft.DurableTask.Worker.AzureManaged`, `Microsoft.Extensions.Hosting` (P07 csproj already brings `Microsoft.Agents.AI.Workflows`). Host + invoke:
  ```csharp
  services.ConfigureDurableWorkflows(
      workflowOptions => workflowOptions.AddWorkflow(workflow),   // singular per both docs; verify plural variant in IntelliSense
      workerBuilder: b => b.UseDurableTaskScheduler(cs),
      clientBuilder: b => b.UseDurableTaskScheduler(cs));
  // ...await host.StartAsync();
  IWorkflowClient client = host.Services.GetRequiredService<IWorkflowClient>();
  IAwaitableWorkflowRun run = (IAwaitableWorkflowRun)await client.RunAsync(workflow, input);
  string? result = await run.WaitForCompletionAsync<string>();
  ```
  HITL (P07 graph has a RequestPort): use `await client.StreamAsync(workflow, input)` — `IStreamingWorkflowRun.WatchStreamAsync()` yields `DurableWorkflowWaitingForInputEvent` (pending port) → answer via `run.SendResponseAsync(evt, response)`; completes with `DurableWorkflowCompletedEvent`.
- Connection string: `Endpoint=http://localhost:8080;TaskHub=default;Authentication=None`.
- Local scheduler backend = DTS emulator: image `mcr.microsoft.com/dts/dts-emulator:latest`, `docker run -d -p 8080:8080 -p 8082:8082 mcr.microsoft.com/dts/dts-emulator:latest` (8080 gRPC scheduler, 8082 dashboard). Plan's earlier `mcr.microsoft.com/durabletask/scheduler-emulator` image does not exist.
- **P07 import caveat**: P07's `BuildWorkflow(store, retriever)` is a private static inside `Program.cs` — not visible to P09. Task 4 extracts it verbatim into a public factory in the P07 project (graph unchanged); durability comes from hosting, not from rewriting the graph.
- Both runnable projects wire OTLP telemetry via AgentCommon's `Telemetry.StartOtlp` (P07 pattern) for the Aspire trace check. NEVER run a service or the emulator in a foreground Bash call — background + tail polling (P08 lesson).
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: Inventory domain (TDD)

**Files:**
- Create: `src/MafDemo.Core/Inventory/InventoryItem.cs`, `src/MafDemo.Core/Inventory/IInventoryStore.cs`, `src/MafDemo.Core/Inventory/InMemoryInventoryStore.cs`
- Test: `tests/MafDemo.Core.Tests/InventoryStoreTests.cs`

**Interfaces:**
- Produces: `record InventoryItem(string Sku, string Model, int Available, int Reserved)`; `interface IInventoryStore { Task<IReadOnlyList<InventoryItem>> ListAsync(); Task<InventoryItem?> GetAsync(string sku); Task<bool> TryReserveAsync(string sku); }`

- [x] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task Reserve_decrements_available_increments_reserved()
{
    var store = new InMemoryInventoryStore();
    store.Seed([new InventoryItem("LT-001", "ThinkPad T14", 3, 0)]);
    var ok = await store.TryReserveAsync("LT-001");
    var item = await store.GetAsync("LT-001");
    Assert.True(ok);
    Assert.Equal((2, 1), (item!.Available, item.Reserved));
}

[Fact]
public async Task Reserve_out_of_stock_fails()
{
    var store = new InMemoryInventoryStore();
    store.Seed([new InventoryItem("LT-002", "MacBook Air", 0, 0)]);
    Assert.False(await store.TryReserveAsync("LT-002"));
}
```

- [x] **Step 2: Run, verify FAIL** — `dotnet test --filter Inventory`

- [x] **Step 3: Implement** — record + interface as above; store = `Dictionary<string, InventoryItem>`, `TryReserveAsync` returns false when `Available == 0`, else `item with { Available-1, Reserved+1 }`. Add `void Seed(IReadOnlyList<InventoryItem>)`.

- [x] **Step 4: Run, verify PASS**

- [x] **Step 5: Commit** — `feat(p09): inventory domain + tests`

### Task 2: Inventory A2A service

**Files:**
- Create: `src/P09.InventoryAgentService/` web project (`Program.cs`, `InventoryTools.cs`, `appsettings.json`)

**Interfaces:**
- Consumes: `IInventoryStore` from Task 1, `OllamaChat.Create()` from P01
- Produces: running A2A endpoint at `http://localhost:5199/a2a/inventory` + agent card

- [x] **Step 1: Scaffold**

```bash
dotnet new web -n P09.InventoryAgentService -o src/P09.InventoryAgentService -f net10.0
dotnet sln add src/P09.InventoryAgentService
dotnet add src/P09.InventoryAgentService reference src/MafDemo.Core
dotnet add src/P09.InventoryAgentService package Microsoft.Agents.AI.Hosting.A2A.AspNetCore --prerelease
dotnet add src/P09.InventoryAgentService package A2A.AspNetCore --prerelease
dotnet add src/P09.InventoryAgentService package OllamaSharp
```

- [x] **Step 2: Tools** (`InventoryTools.cs`)

```csharp
using AIFunctionFactory = Microsoft.Extensions.AI.AIFunctionFactory;
public static class InventoryTools
{
    public static AITool[] All(IInventoryStore store) =>
    [
        AIFunctionFactory.Create((string sku) => store.GetAsync(sku).Result,
            name: "check_stock", description: "Check laptop stock by SKU"),
        AIFunctionFactory.Create((string sku) => store.TryReserveAsync(sku).Result,
            name: "reserve_laptop", description: "Reserve a loaner laptop by SKU"),
    ];
}
```
(Async plumbing: prefer `Func<string, Task<...>>` overloads if `AIFunctionFactory.Create` supports them — adjust.)

- [x] **Step 3: `Program.cs`** — verified pattern (Global Constraints has full snippet):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IInventoryStore>(sp => {
    var s = new InMemoryInventoryStore();
    s.Seed([new("LT-001", "ThinkPad T14", 3, 0), new("LT-002", "MacBook Air", 1, 0)]);
    return s;
});
var client = new ChatClientBuilder(OllamaChat.Create()).UseFunctionInvocation().Build();
var inventory = new ChatClientAgent(client, name: "InventoryAgent",
    instructions: "You answer loaner laptop stock questions using tools only.");

builder.Services.AddKeyedSingleton<AIAgent>("inventory", (_, _) => inventory);
builder.AddA2AServer("inventory");

var app = builder.Build();
app.MapA2AHttpJson("inventory", "/a2a/inventory");
app.MapWellKnownAgentCard(new AgentCard
{
    Name = "InventoryAgent",
    Description = "Loaner laptop stock and reservations.",
    SupportedInterfaces = [new AgentInterface
    {
        Url = "http://localhost:5199/a2a/inventory",
        ProtocolBinding = ProtocolBindingNames.HttpJson,
        ProtocolVersion = "1.0",
    }],
});
app.Run();
```
Bind port 5199 explicitly (`builder.WebHost.UseUrls("http://localhost:5199")` or launchSettings) so client/card URLs are stable.

- [x] **Step 4: Run + verify card** — `dotnet run --project src/P09.InventoryAgentService` (background + tail polling), then
`curl http://localhost:5199/.well-known/agent.json` (docs disagree: probe `/.well-known/agent-card.json` too if 404).
Expected: JSON card naming InventoryAgent.

- [x] **Step 5: Commit** — `feat(p09): inventory agent exposed via a2a`

### Task 3: HelpDesk client consumes remote agent

**Files:**
- Create: `src/P09.HelpDeskClient/` console (`Program.cs`)

- [x] **Step 1: Scaffold + packages**

```bash
dotnet new console -n P09.HelpDeskClient -o src/P09.HelpDeskClient -f net10.0
dotnet sln add src/P09.HelpDeskClient
dotnet add src/P09.HelpDeskClient package Microsoft.Agents.AI.A2A --prerelease
dotnet add src/P09.HelpDeskClient package OllamaSharp
```

- [x] **Step 2: Discover + wrap remote agent** — verified API (MS Learn A2A agent service doc): `A2ACardResolver` takes the remote HOST base URI (it fetches the card from the well-known path — NOT the endpoint path):

```csharp
var resolver = new A2ACardResolver(new Uri("http://localhost:5199"));
AIAgent remote = await resolver.GetAIAgentAsync();  // card + AIAgent in one call
```

- [x] **Step 3: Use remote agent as tool** — helpdesk `ChatClientAgent` over Ollama with the remote `AIAgent` exposed as a function tool via `.AsAIFunction()` (verified extension name; NOT `AsAITool`):

```csharp
var helpdesk = new ChatClientAgent(client, name: "HelpDeskAgent",
    instructions: "...", tools: [remote.AsAIFunction()]);
```

Prompt:

```
"Ticket 4 needs a loaner laptop. Check stock and reserve one if possible."
```

- [x] **Step 4: Two-process run** — start inventory service, then client. Expected: client agent calls remote agent over HTTP, answer reflects real stock ("reserved ThinkPad T14" or "only MacBook available"). Watch inventory service logs for the A2A request.

- [x] **Step 5: Trace check** — both processes exporting OTLP to Aspire dashboard; confirm span crosses the HTTP boundary.

- [x] **Step 6: Commit** — `feat(p09): helpdesk agent calls remote a2a inventory agent`

### Task 4: Durable workflow host

**Files:**
- Create: `src/P09.DurableHost/` console (generic host) — `Program.cs`
- Modify: `src/P07.ResolutionWorkflow/` — extract `BuildWorkflow` verbatim into a public factory (`ResolutionWorkflowFacts.cs`: `public static Workflow Build(ITicketStore store, HandbookRetriever retriever)`); Program.cs calls it. Graph topology/executor ids UNCHANGED.

- [x] **Step 1: Scaffold + verified packages**

```bash
dotnet new console -n P09.DurableHost -o src/P09.DurableHost -f net10.0
dotnet sln add src/P09.DurableHost
dotnet add src/P09.DurableHost reference src/P07.ResolutionWorkflow
dotnet add src/P09.DurableHost package Microsoft.Agents.AI.DurableTask --prerelease
dotnet add src/P09.DurableHost package Microsoft.DurableTask.Client.AzureManaged
dotnet add src/P09.DurableHost package Microsoft.DurableTask.Worker.AzureManaged
dotnet add src/P09.DurableHost package Microsoft.Extensions.Hosting
```

- [x] **Step 1b: Extract P07 factory** — move `BuildWorkflow` body verbatim into `src/P07.ResolutionWorkflow/ResolutionWorkflowFacts.cs`, public static, P07 Program.cs delegates to it. Run P07's own scenario once to confirm nothing broke.

- [x] **Step 2: Start scheduler emulator** (background + tail polling)

```bash
docker run -d -p 8080:8080 -p 8082:8082 mcr.microsoft.com/dts/dts-emulator:latest
```
(8080 = scheduler gRPC, 8082 = dashboard at http://localhost:8082. Verify with `docker ps` + `curl` before continuing.)

- [x] **Step 3: Host + client** — verified pattern (full snippets in Global Constraints):

```csharp
string cs = Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")
    ?? "Endpoint=http://localhost:8080;TaskHub=default;Authentication=None";

var workflow = ResolutionWorkflowFacts.Build(store, retriever);

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.ConfigureDurableWorkflows(
            workflowOptions => workflowOptions.AddWorkflow(workflow),
            workerBuilder: b => b.UseDurableTaskScheduler(cs),
            clientBuilder: b => b.UseDurableTaskScheduler(cs));
    })
    .Build();

await host.StartAsync();
IWorkflowClient client = host.Services.GetRequiredService<IWorkflowClient>();
// P07 graph ends in a RequestPort (FixApproval HITL) → stream, don't RunAsync:
IStreamingWorkflowRun run = await client.StreamAsync(workflow, ticketCtx);
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    switch (evt)
    {
        case DurableWorkflowWaitingForInputEvent req: /* prompt + run.SendResponseAsync(req, decision) */ break;
        case DurableWorkflowCompletedEvent done: /* report */ break;
    }
}
```

Executor caveat: durable executors must be replay-safe — no ambient `Console.WriteLine`/clock/Random inside executors; P07 executors print only via the streaming event loop in Program.cs, so verify and move any stray prints out before hosting durably. Also: RequestPort forwards only the RESPONSE downstream — P07's approval executor already carries its own state, so nothing to persist, but confirm at runtime.

- [x] **Step 4: Run + kill mid-workflow** — schedule a run with a fresh ticket; when the workflow reaches the FixApproval RequestPort (DurableWorkflowWaitingForInputEvent), Ctrl-C the host WITHOUT answering.

- [x] **Step 5: Restart host** — restart, re-attach client to same task hub. Expected: workflow resumes from checkpoint — the paused instance is redelivered by the scheduler; completed steps not re-executed (verify in dashboard/trace).

- [x] **Step 6: Commit** — `feat(p09): durable resolution workflow with kill-and-resume`

### Task 5: Notes

**Files:**
- Create: `docs/projects/09-a2a-durable/NOTES.md`

- [x] **Step 1: NOTES.md** — bullets: A2A discovery flow (card at well-known path → HTTP+JSON binding), what changed vs in-process agent-tool from P06, durable checkpoint resume semantics (executors = activities, RequestPort = specialized dispatch), what the DTS emulator is standing in for, plus every runtime divergence from this plan's verified citations.
- [x] **Step 2: Commit** — `docs(p09): learning notes`