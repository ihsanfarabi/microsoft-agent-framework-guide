# P01: HelloAgent — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Solution skeleton + TDD shared domain core + minimal MAF FAQ agent over Ollama, streamed, with OTel console traces.

**Architecture:** One solution. `MafDemo.Core` holds pure domain (Ticket, store). P01 console wires `OllamaApiClient` → `ChatClientAgent`, two entry modes (one-shot, streaming). No tools, no session — deliberately minimal.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` (prerelease), `OllamaSharp`, `OpenTelemetry`, xUnit.

**Spec:** `docs/projects/01-hello-agent/SPEC.md`

## Global Constraints

- Model: `glm-5.3-flash:cloud` via Ollama at `http://localhost:11434` (env `OLLAMA_ENDPOINT` overrides).
- Package source: `dotnet add package Microsoft.Agents.AI --prerelease`.
- MAF class names cited per task from official docs — if a name in this plan errors, copy the exact name from the cited doc sample and fix the plan.
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: Solution + Core domain (TDD)

**Files:**
- Create: `MafDemo.sln`
- Create: `src/MafDemo.Core/MafDemo.Core.csproj` + `src/MafDemo.Core/Domain/Ticket.cs`, `src/MafDemo.Core/Stores/ITicketStore.cs`, `src/MafDemo.Core/Stores/InMemoryTicketStore.cs`
- Create: `tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj`

**Interfaces:**
- Produces (used by P02+):
  - `enum TicketStatus { Open, InProgress, Resolved, Closed }`
  - `enum TicketPriority { Low, Normal, High, Critical }`
  - `record Ticket(Guid Id, string Title, string Description, TicketPriority Priority, TicketStatus Status, string? Assignee, DateTimeOffset CreatedAt, IReadOnlyList<string> Notes)`
  - `interface ITicketStore { Task<Ticket> CreateAsync(string title, string description, TicketPriority priority); Task<Ticket?> GetAsync(Guid id); Task<IReadOnlyList<Ticket>> ListAsync(); Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status); Task AddNoteAsync(Guid id, string note); }`
  - `class InMemoryTicketStore : ITicketStore`

- [ ] **Step 1: Scaffold**

```bash
dotnet new sln -n MafDemo
dotnet new classlib -n MafDemo.Core -o src/MafDemo.Core -f net10.0
dotnet new xunit -n MafDemo.Core.Tests -o tests/MafDemo.Core.Tests -f net10.0
dotnet sln add src/MafDemo.Core tests/MafDemo.Core.Tests
dotnet add tests/MafDemo.Core.Tests reference src/MafDemo.Core
```

- [ ] **Step 2: Write failing tests**

```csharp
// tests/MafDemo.Core.Tests/InMemoryTicketStoreTests.cs
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

public class InMemoryTicketStoreTests
{
    [Fact]
    public async Task Create_assigns_id_and_open_status()
    {
        var store = new InMemoryTicketStore();
        var ticket = await store.CreateAsync("VPN broken", "Cannot connect", TicketPriority.High);
        Assert.NotEqual(Guid.Empty, ticket.Id);
        Assert.Equal(TicketStatus.Open, ticket.Status);
    }

    [Fact]
    public async Task AddNote_appends_and_roundtrips()
    {
        var store = new InMemoryTicketStore();
        var t = await store.CreateAsync("t", "d", TicketPriority.Normal);
        await store.AddNoteAsync(t.Id, "tried restart");
        var loaded = await store.GetAsync(t.Id);
        Assert.Contains("tried restart", loaded!.Notes);
    }

    [Fact]
    public async Task UpdateStatus_persists()
    {
        var store = new InMemoryTicketStore();
        var t = await store.CreateAsync("t", "d", TicketPriority.Normal);
        var updated = await store.UpdateStatusAsync(t.Id, TicketStatus.InProgress);
        Assert.Equal(TicketStatus.InProgress, updated!.Status);
    }
}
```

- [ ] **Step 3: Run tests, verify FAIL**

Run: `dotnet test`
Expected: compile error — types missing.

- [ ] **Step 4: Implement Core**

```csharp
// src/MafDemo.Core/Domain/Ticket.cs
namespace MafDemo.Core.Domain;
public enum TicketStatus { Open, InProgress, Resolved, Closed }
public enum TicketPriority { Low, Normal, High, Critical }
public record Ticket(Guid Id, string Title, string Description,
    TicketPriority Priority, TicketStatus Status, string? Assignee,
    DateTimeOffset CreatedAt, IReadOnlyList<string> Notes);

// src/MafDemo.Core/Stores/ITicketStore.cs
using MafDemo.Core.Domain;
namespace MafDemo.Core.Stores;
public interface ITicketStore
{
    Task<Ticket> CreateAsync(string title, string description, TicketPriority priority);
    Task<Ticket?> GetAsync(Guid id);
    Task<IReadOnlyList<Ticket>> ListAsync();
    Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status);
    Task AddNoteAsync(Guid id, string note);
}

// src/MafDemo.Core/Stores/InMemoryTicketStore.cs — dictionary-backed, lock not needed (single-threaded demo); update via `ticket with { ... }`
```

- [ ] **Step 5: Run tests, verify PASS** — `dotnet test`

- [ ] **Step 6: Commit** — `feat(core): ticket domain + in-memory store`

### Task 2: Ollama client factory + FaqBot one-shot

**Files:**
- Create: `src/P01.HelloAgent/P01.HelloAgent.csproj` + `Program.cs`, `OllamaChat.cs`, `FaqBot.cs`
- Create: `src/P01.HelloAgent/appsettings.json`

**Interfaces:**
- Produces: `static class OllamaChat { public static IChatClient Create(string? model = null); }` — P02+ reuse pattern
- Produces: `FaqBot` agent factory

- [ ] **Step 1: Scaffold console project**

```bash
dotnet new console -n P01.HelloAgent -o src/P01.HelloAgent -f net10.0
dotnet sln add src/P01.HelloAgent
dotnet add src/P01.HelloAgent package Microsoft.Agents.AI --prerelease
dotnet add src/P01.HelloAgent package OllamaSharp
dotnet add src/P01.HelloAgent package Microsoft.Extensions.OpenTelemetry
```

- [ ] **Step 2: Write `OllamaChat.cs`**

```csharp
using Microsoft.Extensions.AI;
using OllamaSharp;

public static class OllamaChat
{
    public static IChatClient Create(string? model = null)
    {
        var endpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434";
        return new OllamaApiClient(new Uri(endpoint), model ?? "glm-5.3-flash:cloud");
    }
}
```

- [ ] **Step 3: Write `FaqBot.cs`** — class name check: cite https://learn.microsoft.com/en-us/agent-framework/get-started/your-first-agent and the .NET intro blog (`ChatClientAgent`):

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public static class FaqBot
{
    public static ChatClientAgent Create(string instructions) =>
        new(OllamaChat.Create(), name: "FaqBot", instructions: instructions);
}
```

- [ ] **Step 4: One-shot in `Program.cs`**

```csharp
var agent = FaqBot.Create("You are HelpDeskHQ's FAQ bot. Answer IT questions in one short paragraph.");
var result = await agent.RunAsync("How do I connect to the company Wi-Fi?");
Console.WriteLine(result);
```
(RunAsync return + await pattern: verify against doc sample; adjust if API is `agent.RunAsync(...)` returning task with `.Text`.)

- [ ] **Step 5: Run**

Run: `dotnet run --project src/P01.HelloAgent`
Expected: Wi-Fi answer paragraph. If Ollama connection refused: `ollama serve`, re-run.

- [ ] **Step 6: Commit** — `feat(p01): faq bot one-shot over ollama`

### Task 3: Streaming

**Files:**
- Modify: `src/P01.HelloAgent/Program.cs`

- [ ] **Step 1: Add streaming path** — `RunStreamingAsync` (name from docs):

```csharp
await foreach (var update in agent.RunStreamingAsync("Explain how to reset my password in 3 steps."))
    Console.Write(update.Text);
```
(Exact update member: check doc sample — `.Text` or `ToString()`.)

- [ ] **Step 2: Run** — tokens appear incrementally, not one dump.

- [ ] **Step 3: Commit** — `feat(p01): streaming responses`

### Task 4: OTel console traces

**Files:**
- Modify: `src/P01.HelloAgent/Program.cs`
- Create: `src/P01.HelloAgent/Telemetry.cs`

- [ ] **Step 1: Wire OTel** — per MAF observability doc (https://learn.microsoft.com/en-us/agent-framework/agents/observability): `TraceProvider` + console exporter; add source name used by MAF (`Microsoft.Agents.AI*` — copy exact source name from doc).

- [ ] **Step 2: Run** — expect span lines on stdout for the model call.

- [ ] **Step 3: Commit** — `feat(p01): opentelemetry console traces`

### Task 5: Instructions experiment + notes

**Files:**
- Create: `docs/projects/01-hello-agent/NOTES.md`

- [ ] **Step 1: Experiment** — run same prompt with `"Answer only in bullet points, max 3."` vs default instructions. Record both outputs in NOTES.md.

- [ ] **Step 2: NOTES.md** — 3 bullets: what the agent loop did, what the trace showed, what instructions changed.

- [ ] **Step 3: Commit** — `docs(p01): learning notes`