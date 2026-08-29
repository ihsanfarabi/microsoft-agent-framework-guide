# P09: A2aDurable — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Inventory agent exposed over A2A from a separate ASP.NET Core service + P07 resolution workflow made durable (kill/restart resumes).

**Architecture:** Three projects: inventory service (A2A server), helpdesk client (A2A client over Ollama), durable host (generic host + Durable Task worker running P07 workflow against local scheduler emulator).

**Tech Stack:** .NET 10, `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`, `Microsoft.Agents.AI.A2A`, `Microsoft.Agents.AI.DurableTask` + `Microsoft.DurableTask.*.AzureManaged` (all prerelease), OllamaSharp, xUnit.

**Spec:** `docs/projects/09-a2a-durable/SPEC.md`

## Global Constraints

- Verified server packages: `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` (pulls `Microsoft.Agents.AI.Hosting.A2A`) + `A2A.AspNetCore --prerelease`. Server pattern: `builder.AddAIAgent("inventory", ...)` then `app.MapA2A(agent, "/a2a/inventory")`.
- Verified client package: `Microsoft.Agents.AI.A2A` — `A2AAgent` wraps any A2A endpoint as standard `AIAgent` (`RunAsync`/`RunStreamingAsync` work).
- Verified durable BYO packages: `Microsoft.Agents.AI.DurableTask --prerelease`, `Microsoft.DurableTask.Client.AzureManaged`, `Microsoft.DurableTask.Worker.AzureManaged`, `Microsoft.Extensions.Hosting`; host pattern: `Host.CreateDefaultBuilder` + `services.ConfigureDurableWorkflows(options => options.AddWorkflows(workflow), workerBuilder: b => b.UseDurableTaskScheduler(cs), clientBuilder: b => b.UseDurableTaskScheduler(cs))`; connection string `Endpoint=http://localhost:8080;TaskHub=default;Authentication=None`.
- Local scheduler backend = Durable Task Scheduler emulator container: `docker run -d -p 8080:8080 mcr.microsoft.com/durabletask/scheduler-emulator` (verify image tag on https://learn.microsoft.com/en-us/azure/durable-task/scheduler-emulator before first run).
- P07 workflow definition imported as-is; durability added by hosting, not by rewriting the graph.
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: Inventory domain (TDD)

**Files:**
- Create: `src/MafDemo.Core/Inventory/InventoryItem.cs`, `src/MafDemo.Core/Inventory/IInventoryStore.cs`, `src/MafDemo.Core/Inventory/InMemoryInventoryStore.cs`
- Test: `tests/MafDemo.Core.Tests/InventoryStoreTests.cs`

**Interfaces:**
- Produces: `record InventoryItem(string Sku, string Model, int Available, int Reserved)`; `interface IInventoryStore { Task<IReadOnlyList<InventoryItem>> ListAsync(); Task<InventoryItem?> GetAsync(string sku); Task<bool> TryReserveAsync(string sku); }`

- [ ] **Step 1: Write failing tests**

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

- [ ] **Step 2: Run, verify FAIL** — `dotnet test --filter Inventory`

- [ ] **Step 3: Implement** — record + interface as above; store = `Dictionary<string, InventoryItem>`, `TryReserveAsync` returns false when `Available == 0`, else `item with { Available-1, Reserved+1 }`. Add `void Seed(IReadOnlyList<InventoryItem>)`.

- [ ] **Step 4: Run, verify PASS**

- [ ] **Step 5: Commit** — `feat(p09): inventory domain + tests`

### Task 2: Inventory A2A service

**Files:**
- Create: `src/P09.InventoryAgentService/` web project (`Program.cs`, `InventoryTools.cs`, `appsettings.json`)

**Interfaces:**
- Consumes: `IInventoryStore` from Task 1, `OllamaChat.Create()` from P01
- Produces: running A2A endpoint at `http://localhost:5199/a2a/inventory` + agent card

- [ ] **Step 1: Scaffold**

```bash
dotnet new web -n P09.InventoryAgentService -o src/P09.InventoryAgentService -f net10.0
dotnet sln add src/P09.InventoryAgentService
dotnet add src/P09.InventoryAgentService reference src/MafDemo.Core
dotnet add src/P09.InventoryAgentService package Microsoft.Agents.AI.Hosting.A2A.AspNetCore --prerelease
dotnet add src/P09.InventoryAgentService package A2A.AspNetCore --prerelease
dotnet add src/P09.InventoryAgentService package OllamaSharp
```

- [ ] **Step 2: Tools** (`InventoryTools.cs`)

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

- [ ] **Step 3: `Program.cs`** — verified pattern:

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
builder.AddAIAgent("inventory", (sp, key) => inventory); // verify signature vs doc sample
var app = builder.Build();
app.MapA2A(inventory, "/a2a/inventory");
app.Run();
```
(Exact `AddAIAgent`/`MapA2A` signatures: copy from https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/a2a/dotnet sample.)

- [ ] **Step 4: Run + verify card** — `dotnet run --project src/P09.InventoryAgentService`, then
`curl http://localhost:5199/a2a/inventory/.well-known/agent-card.json` (path per A2A spec; check doc if 404 — may be `/.well-known/agent.json`).
Expected: JSON card naming InventoryAgent.

- [ ] **Step 5: Commit** — `feat(p09): inventory agent exposed via a2a`

### Task 3: HelpDesk client consumes remote agent

**Files:**
- Create: `src/P09.HelpDeskClient/` console (`Program.cs`)

- [ ] **Step 1: Scaffold + packages**

```bash
dotnet new console -n P09.HelpDeskClient -o src/P09.HelpDeskClient -f net10.0
dotnet sln add src/P09.HelpDeskClient
dotnet add src/P09.HelpDeskClient package Microsoft.Agents.AI.A2A --prerelease
dotnet add src/P09.HelpDeskClient package OllamaSharp
```

- [ ] **Step 2: Discover + wrap remote agent** — client creation API: copy discovery code from https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/agent-services/a2a (well-known URI strategy). Sketch:

```csharp
// verify exact factory/discovery call from doc
AIAgent remote = A2AAgentFactory.CreateFromWellKnownUri(
    new Uri("http://localhost:5199/a2a/inventory"));  // name to fix from doc
```

- [ ] **Step 3: Use remote agent as tool** — helpdesk `ChatClientAgent` over Ollama with the remote `AIAgent` exposed as agent-tool (`remote.AsAITool(...)` — verify extension from "Agents as tools" doc https://learn.microsoft.com/en-us/agent-framework/journey/agents-as-tools). Prompt:

```
"Ticket 4 needs a loaner laptop. Check stock and reserve one if possible."
```

- [ ] **Step 4: Two-process run** — start inventory service, then client. Expected: client agent calls remote agent over HTTP, answer reflects real stock ("reserved ThinkPad T14" or "only MacBook available"). Watch inventory service logs for the A2A request.

- [ ] **Step 5: Trace check** — both processes exporting OTLP to Aspire dashboard; confirm span crosses the HTTP boundary.

- [ ] **Step 6: Commit** — `feat(p09): helpdesk agent calls remote a2a inventory agent`

### Task 4: Durable workflow host

**Files:**
- Create: `src/P09.DurableHost/` console (generic host) — `Program.cs`
- Modify: nothing in P07 workflow definition (import as-is)

- [ ] **Step 1: Scaffold + verified packages**

```bash
dotnet new console -n P09.DurableHost -o src/P09.DurableHost -f net10.0
dotnet sln add src/P09.DurableHost
dotnet add src/P09.DurableHost reference src/P07.ResolutionWorkflow
dotnet add src/P09.DurableHost package Microsoft.Agents.AI.DurableTask --prerelease
dotnet add src/P09.DurableHost package Microsoft.DurableTask.Client.AzureManaged
dotnet add src/P09.DurableHost package Microsoft.DurableTask.Worker.AzureManaged
dotnet add src/P09.DurableHost package Microsoft.Extensions.Hosting
```

- [ ] **Step 2: Start scheduler emulator**

```bash
docker run -d -p 8080:8080 mcr.microsoft.com/durabletask/scheduler-emulator
```
(Verify tag/startup on Durable Task Scheduler emulator docs; container must serve `http://localhost:8080`.)

- [ ] **Step 3: Host** — verified pattern:

```csharp
string cs = Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")
    ?? "Endpoint=http://localhost:8080;TaskHub=default;Authentication=None";

var workflow = ResolutionWorkflowFacts.Build(); // P07 factory — rename to actual P07 builder

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.ConfigureDurableWorkflows(
            options => options.AddWorkflows(workflow),
            workerBuilder: b => b.UseDurableTaskScheduler(cs),
            clientBuilder: b => b.UseDurableTaskScheduler(cs));
    })
    .Build();

await host.StartAsync;
// then: schedule a workflow run via the durable client (verify client invocation
// API from dotnet/samples/04-hosting/DurableWorkflows/ConsoleApps) and keep host alive
```

- [ ] **Step 4: Run + kill mid-workflow** — schedule run with a new ticket; when the workflow reaches the HITL approval step (or mid-diagnosis), Ctrl-C the host.

- [ ] **Step 5: Restart host** — restart, re-attach client to same task hub. Expected: workflow resumes from checkpoint — completed steps not re-executed (verify in trace; the durable samples show the client reconnect pattern).

- [ ] **Step 6: Commit** — `feat(p09): durable resolution workflow with kill-and-resume`

### Task 5: Notes

**Files:**
- Create: `docs/projects/09-a2a-durable/NOTES.md`

- [ ] **Step 1: NOTES.md** — bullets: A2A discovery flow (card → JSON-RPC), what changed vs in-process agent-tool from P06, durable checkpoint resume semantics, what the emulator is standing in for.
- [ ] **Step 2: Commit** — `docs(p09): learning notes`