# P02: TicketTools — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Conversational ticket management via function tools + an MCP server, tool loop visible in traces.

**Architecture:** `TicketToolFunctions` wraps `ITicketStore` (pure, TDD). Agent factory wires tools through `UseFunctionInvocation`. MCP tools loaded from a stdio server and merged. Console scenarios verify end to end.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` (prerelease), `OllamaSharp`, `ModelContextProtocol` (C# SDK), OpenTelemetry, xUnit.

**Spec:** `docs/projects/02-ticket-tools/SPEC.md`

## Global Constraints

- Model: `glm-5.3-flash:cloud` via `OllamaChat.Create()` (P01 pattern).
- **Tool calling requires `new ChatClientBuilder(raw).UseFunctionInvocation().Build()`** — a raw `OllamaApiClient` will not execute tools.
- Tools registered via `AIFunctionFactory.Create(method)` per https://learn.microsoft.com/en-us/agent-framework/agents/tools.
- MCP per https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/tools/mcp: `McpClient` → `ListToolsAsync()` → cast to `AITool` → pass to agent.
- Class names cited from docs — if a name errors, copy exact name from cited doc sample and fix the plan.
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: Tool wrapper class (TDD)

**Files:**
- Create: `src/P02.TicketTools/P02.TicketTools.csproj` (console, refs `MafDemo.Core`)
- Create: `src/P02.TicketTools/TicketToolFunctions.cs`
- Create: `tests/P02.TicketTools.Tests/` xunit project referencing P02 project + `MafDemo.Core`
- Modify: `MafDemo.sln`

**Interfaces:**
- Produces (used by Tasks 2–4 and P03+):
  - `class TicketToolFunctions` ctor `TicketToolFunctions(ITicketStore store)` with public methods:
    - `Task<string> CreateTicketAsync(string title, string description, string priority)` — returns "Created ticket <id> (priority <p>)"
    - `Task<string> ListTicketsAsync()` — returns one line per ticket, `"(none)"` if empty
    - `Task<string> UpdateTicketStatusAsync(string id, string status)` — parses Guid + enum, returns updated or "not found"
    - `Task<string> AddTicketNoteAsync(string id, string note)`
- Consumes: `MafDemo.Core` `ITicketStore`, `Ticket`, enums (P01 Task 1)

- [ ] **Step 1: Scaffold**

```bash
dotnet new console -n P02.TicketTools -o src/P02.TicketTools -f net10.0
dotnet new xunit -n P02.TicketTools.Tests -o tests/P02.TicketTools.Tests -f net10.0
dotnet sln add src/P02.TicketTools tests/P02.TicketTools.Tests
dotnet add src/P02.TicketTools reference src/MafDemo.Core
dotnet add tests/P02.TicketTools.Tests reference src/P02.TicketTools src/MafDemo.Core
dotnet add src/P02.TicketTools package Microsoft.Agents.AI --prerelease
dotnet add src/P02.TicketTools package OllamaSharp
```

- [ ] **Step 2: Write failing tests** (no LLM — store logic only)

```csharp
// tests/P02.TicketTools.Tests/TicketToolFunctionsTests.cs
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

public class TicketToolFunctionsTests
{
    [Fact]
    public async Task CreateTicket_returns_id_and_priority()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        var result = await f.CreateTicketAsync("VPN broken", "cannot connect", "High");
        Assert.Contains("High", result);
        Assert.Contains("ticket", result.ToLower());
    }

    [Fact]
    public async Task ListTickets_empty_store_returns_none()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        Assert.Contains("none", (await f.ListTicketsAsync()).ToLower());
    }

    [Fact]
    public async Task ListTickets_lists_created()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        await f.CreateTicketAsync("VPN", "d", "High");
        var listing = await f.ListTicketsAsync();
        Assert.Contains("VPN", listing);
    }

    [Fact]
    public async Task UpdateTicketStatus_unknown_id_says_not_found()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        var result = await f.UpdateTicketStatusAsync(Guid.NewGuid().ToString(), "Resolved");
        Assert.Contains("not found", result.ToLower());
    }
}
```

- [ ] **Step 3: Run tests, verify FAIL**

Run: `dotnet test tests/P02.TicketTools.Tests`
Expected: compile error — class missing.

- [ ] **Step 4: Implement**

```csharp
// src/P02.TicketTools/TicketToolFunctions.cs
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

public class TicketToolFunctions(ITicketStore store)
{
    public async Task<string> CreateTicketAsync(string title, string description, string priority)
    {
        var p = Enum.TryParse<TicketPriority>(priority, ignoreCase: true, out var parsed) ? parsed : TicketPriority.Normal;
        var t = await store.CreateAsync(title, description, p);
        return $"Created ticket {t.Id} (priority {t.Priority})";
    }

    public async Task<string> ListTicketsAsync()
    {
        var tickets = await store.ListAsync();
        return tickets.Count == 0 ? "(none)"
            : string.Join("\n", tickets.Select(t => $"{t.Id} | {t.Status} | {t.Priority} | {t.Title}"));
    }

    public async Task<string> UpdateTicketStatusAsync(string id, string status)
    {
        if (!Guid.TryParse(id, out var guid) || !Enum.TryParse<TicketStatus>(status, ignoreCase: true, out var st))
            return "Invalid id or status.";
        var updated = await store.UpdateStatusAsync(guid, st);
        return updated is null ? $"Ticket {id} not found" : $"Ticket {id} now {updated.Status}";
    }

    public async Task<string> AddTicketNoteAsync(string id, string note)
    {
        if (!Guid.TryParse(id, out var guid)) return "Invalid id.";
        var ticket = await store.GetAsync(guid);
        if (ticket is null) return $"Ticket {id} not found";
        await store.AddNoteAsync(guid, note);
        return $"Note added to {id}.";
    }
}
```

- [ ] **Step 5: Run tests, verify PASS** — `dotnet test tests/P02.TicketTools.Tests`

- [ ] **Step 6: Commit** — `feat(p02): ticket tool functions with tests`

### Task 2: Agent with tools

**Files:**
- Create: `src/P02.TicketTools/TicketBot.cs`
- Modify: `src/P02.TicketTools/Program.cs`

**Interfaces:**
- Consumes: `TicketToolFunctions` (Task 1), `OllamaChat.Create()` (P01)
- Produces: `static class TicketBot { public static ChatClientAgent Create(); }`

- [ ] **Step 1: Write agent factory** — tool pipeline is the verified pattern:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;

public static class TicketBot
{
    public static ChatClientAgent Create(ITicketStore store)
    {
        var tools = new TicketToolFunctions(store);
        IChatClient chatClient = new ChatClientBuilder(new OllamaApiClient(
            new Uri(Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434"),
            "glm-5.3-flash:cloud"))
            .UseFunctionInvocation()   // REQUIRED for the tool loop
            .Build();

        return new ChatClientAgent(chatClient, name: "TicketBot",
            instructions: "You are HelpDeskHQ's ticket bot. Create, list, update tickets via tools. Always echo back ticket IDs.")
        {
            // tool registration: verify exact property/members from tools doc
            // pattern in docs: ChatOptions/AIFunctionTools on agent options — copy from
            // https://learn.microsoft.com/en-us/agent-framework/agents/tools sample
        };
    }
}
```

- [ ] **Step 2: Wire tools per tools doc sample** — likely `new ChatClientAgent(chatClient, options: new ChatClientAgentOptions { ChatOptions = new() { Tools = [ AIFunctionFactory.Create(tools.CreateTicketAsync), ... ] } })`. Copy exact syntax from the doc sample; register all four methods.

- [ ] **Step 3: Scripted scenario in `Program.cs`**

```csharp
var store = new InMemoryTicketStore();
var agent = TicketBot.Create(store);
Console.WriteLine(await agent.RunAsync("File a ticket for my broken VPN, priority high."));
Console.WriteLine(await agent.RunAsync("List my tickets."));
Console.WriteLine(await agent.RunAsync("Mark the VPN ticket resolved."));
```

- [ ] **Step 4: Run**

Run: `dotnet run --project src/P02.TicketTools`
Expected: ticket ID echoed, listing shows it, status updated. Verify store state printed at exit (`store.ListAsync()` — 1 ticket, Resolved).

- [ ] **Step 5: Commit** — `feat(p02): ticket bot with function tools`

### Task 3: Trace the tool loop

**Files:**
- Modify: `src/P02.TicketTools/Program.cs` (add OTel console wiring, P01 Task 4 pattern)

- [ ] **Step 1: Enable OTel console exporter** (reuse P01 `Telemetry.cs` pattern).

- [ ] **Step 2: Run scenario** — expect spans showing: model call → function invocation → model call. Record the function-call span names in `NOTES.md`.

- [ ] **Step 3: Commit** — `docs(p02): tool loop trace notes`

### Task 4: MCP server tools

**Files:**
- Create: `sandbox/readme.txt` (test content)
- Modify: `src/P02.TicketTools/TicketBot.cs` + `Program.cs`

- [ ] **Step 1: Add package** — `dotnet add src/P02.TicketTools package ModelContextProtocol` (name per MCP integration doc: https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/tools/mcp).

- [ ] **Step 2: Pick stdio server** — filesystem server via npx: create `sandbox/` with `readme.txt` inside; server command `npx -y @modelcontextprotocol/server-filesystem <abs-path-to-sandbox>`.

- [ ] **Step 3: Wire MCP client** — per doc pattern:

```csharp
// sketch — copy exact client creation from MCP integration doc sample
var mcpClient = await McpClientFactory.CreateAsync(/* stdio transport, server command */);
IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync();
// cast to AITool and merge with function tools in agent options
```

- [ ] **Step 4: Scenario** — add run: `"What files are in the sandbox and what does the readme say?"`
Expected: agent lists files via MCP tool, summarizes readme.

- [ ] **Step 5: Run, verify. Commit** — `feat(p02): mcp server tools merged into ticket bot`

### Task 5: Wrap-up

**Files:**
- Create: `docs/projects/02-ticket-tools/NOTES.md`

- [ ] **Step 1: NOTES.md** — bullets: how the model chose tools; difference between function tools and MCP tools from the app's perspective; one trace observation.
- [ ] **Step 2 (stretch):** custom C# MCP server exposing `search_knowledge` over handbook corpus (seed for P04).
- [ ] **Step 3: Commit** — `docs(p02): wrap-up notes`