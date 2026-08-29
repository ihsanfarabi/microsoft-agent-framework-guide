# P08: HarnessAgent — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Overnight batch agent on the Agent Harness: todos, file access/memory, approvals, kill-and-resume.

**Architecture:** Console app seeds `FileTicketStore` with 5 backlog tickets, builds harness via `AsHarnessAgent`, runs batch, persists session state between runs. Approval policy = pure function under test.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` (prerelease), `OllamaSharp`, xUnit. Harness API verified from devblogs (2026-08).

**Spec:** `docs/projects/08-harness-agent/SPEC.md`

## Global Constraints

- Model `glm-5.3-flash:cloud` via `OllamaChat.Create()`; harness tools need function invocation — wrap: `new ChatClientBuilder(OllamaChat.Create()).UseFunctionInvocation().Build()` before `AsHarnessAgent` (verify against harness blog whether harness wires this itself).
- Ticket tools reused from P02 (`create_ticket`, `list_tickets`, `add_note`, `close_ticket`) via `AIFunctionFactory.Create` / DI `AddAIAgent`.
- Harness API (verified): `chatClient.AsHarnessAgent(new HarnessAgentOptions { FileAccessStore = new FileSystemAgentFileStore(...), ChatOptions = new ChatOptions { ... } })`; approvals via `builder.UseToolApproval(...)` + `ApprovalRequiredAIFunction`; `FileMemoryProvider` on by default under `agent-file-memory/<session-id>/`.
- Session state: reuse one session across turns; serialize to survive restart (verify method names against session doc).
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: Scaffold + seed backlog (TDD)

**Files:**
- Create: `src/P08.HarnessAgent/` console (`P08.HarnessAgent.csproj`, `Program.cs`)
- Create: `src/P08.HarnessAgent/Seed.cs` — writes 5 backlog tickets into `work/tickets.json` via `FileTicketStore`
- Test: `tests/P08.HarnessAgent.Tests/SeedTests.cs`

**Interfaces:**
- Produces: `static class Seed { public static async Task RunAsync(FileTicketStore store); }` — idempotent: skips if ≥5 tickets exist

- [ ] **Step 1: Scaffold**

```bash
dotnet new console -n P08.HarnessAgent -o src/P08.HarnessAgent -f net10.0
dotnet sln add src/P08.HarnessAgent
dotnet add src/P08.HarnessAgent reference src/MafDemo.Core
dotnet add src/P08.HarnessAgent package Microsoft.Agents.AI --prerelease
dotnet new xunit -n P08.HarnessAgent.Tests -o tests/P08.HarnessAgent.Tests -f net10.0
dotnet sln add tests/P08.HarnessAgent.Tests
dotnet add tests/P08.HarnessAgent.Tests reference src/M08 2>/dev/null || dotnet add tests/P08.HarnessAgent.Tests reference src/P08.HarnessAgent
```
(Console projects aren't referenced by test projects directly — instead move `Seed` into `MafDemo.Core/Seed/BacklogSeed.cs` and reference Core from tests.)

- [ ] **Step 2: Write failing test** (in `tests/MafDemo.Core.Tests/BacklogSeedTests.cs`)

```csharp
[Fact]
public async Task Seed_creates_five_tickets_and_is_idempotent()
{
    var dir = Path.Combine(Path.GetTempPath(), $"p08-{Guid.NewGuid():N}");
    var store = new FileTicketStore(dir);
    await BacklogSeed.RunAsync(store);
    await BacklogSeed.RunAsync(store); // second run must not duplicate
    Assert.Equal(5, (await store.ListAsync()).Count);
}
```

- [ ] **Step 3: Run, verify FAIL** — `dotnet test --filter BacklogSeed`

- [ ] **Step 4: Implement** `src/MafDemo.Core/Seed/BacklogSeed.cs`:

```csharp
namespace MafDemo.Core.Seed;
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
public static class BacklogSeed
{
    private static readonly (string Title, string Desc, TicketPriority P)[] Backlog =
    [
        ("VPN fails from home network", "Cannot connect since router change", TicketPriority.High),
        ("Outlook calendar not syncing", "Meetings missing on phone", TicketPriority.Normal),
        ("Laptop fan running loud", "Overheating during builds", TicketPriority.Normal),
        ("Cannot install Python 3.12", "UAC blocks pip", TicketPriority.Low),
        ("Printer offline in pod 4", "Queue stuck", TicketPriority.Low),
    ];

    public static async Task RunAsync(ITicketStore store)
    {
        if ((await store.ListAsync()).Count >= Backlog.Length) return;
        foreach (var (title, desc, p) in Backlog)
            await store.CreateAsync(title, desc, p);
    }
}
```

- [ ] **Step 5: Run, verify PASS** — `dotnet test --filter BacklogSeed`

- [ ] **Step 6: Commit** — `feat(p08): backlog seed with idempotency test`

### Task 2: Harness agent + batch scenario

**Files:**
- Create: `src/P08.HarnessAgent/HarnessFacts.cs`, `src/P08.HarnessAgent/Program.cs`

**Interfaces:**
- Produces: `static class HarnessFacts { public static AIAgent Build(IChatClient client, AITool[] tools); }`

- [ ] **Step 1: Write `HarnessFacts.cs`** — verified API:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public static class HarnessFacts
{
    public static AIAgent Build(IChatClient client, AITool[] tools)
    {
        return client.AsHarnessAgent(new HarnessAgentOptions
        {
            FileAccessStore = new FileSystemAgentFileStore(
                Path.Combine(AppContext.BaseDirectory, "work")),
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are HelpDeskHQ's overnight agent. For each ticket in the backlog:
                    1) track it in your todo list, 2) consult handbook docs in work/handbook/,
                    3) add a resolution note to the ticket, 4) request approval to close it.
                    Finish all tickets before reporting a summary.
                    """,
                Tools = tools,
            },
        });
    }
}
```

- [ ] **Step 2: Wire `Program.cs`** — build function-invoking client, resolve ticket tools (P02 factories over `FileTicketStore`), seed, create session, run `"Work the ticket backlog."`:

```csharp
var client = new ChatClientBuilder(OllamaChat.Create()).UseFunctionInvocation().Build();
var store = new FileTicketStore("work");
await BacklogSeed.RunAsync(store);
var agent = HarnessFacts.Build(client, TicketToolsFacts.All(store));
var session = agent.CreateSession();  // verify session API on AIAgent from session doc
var result = await agent.RunAsync("Work the ticket backlog.", session);
Console.WriteLine(result);
```

- [ ] **Step 3: Run** — `dotnet run --project src/P08.HarnessAgent`
Expected: agent plans (todo tool calls), reads handbook files (`file_access_*`), adds notes per ticket. Watch `agent-file-memory/<session>/` populate.

- [ ] **Step 4: Commit** — `feat(p08): harness batch scenario`

### Task 3: Approval gate (TDD)

**Files:**
- Create: `src/P08.HarnessAgent/ApprovalPolicy.cs` (move to Core if test project can't reference console)
- Create: `src/P08.HarnessAgent/CloseTicketTool.cs` wrapper
- Test: `tests/P08.HarnessAgent.Tests/ApprovalPolicyTests.cs` (or Core.Tests)

**Interfaces:**
- Produces: `static class ApprovalPolicy { public static bool ShouldAutoApprove(FunctionCallContent call); }`
- Consumes: P02 `close_ticket` tool function

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public void Read_only_tools_auto_approve()
{
    var call = new FunctionCallContent("callId", "list_tickets", new Dictionary<string, object?>());
    Assert.True(ApprovalPolicy.ShouldAutoApprove(call));
}

[Fact]
public void Close_ticket_needs_human()
{
    var call = new FunctionCallContent("callId", "close_ticket", new Dictionary<string, object?>());
    Assert.False(ApprovalPolicy.ShouldAutoApprove(call));
}
```

- [ ] **Step 2: Run, verify FAIL**

- [ ] **Step 3: Implement**

```csharp
using Microsoft.Extensions.AI;
public static class ApprovalPolicy
{
    private static readonly HashSet<string> ReadOnly = ["list_tickets", "get_ticket", "add_note"];
    public static bool ShouldAutoApprove(FunctionCallContent call) => ReadOnly.Contains(call.Name);
}
```

- [ ] **Step 4: Run, verify PASS**

- [ ] **Step 5: Wire approvals** — verified pattern from harness blog: wrap close tool with `new ApprovalRequiredAIFunction(AIFunctionFactory.Create(closeFn, "close_ticket"))`, apply `builder.UseToolApproval(new ToolApprovalAgentOptions { AutoApprovalRules = [ApprovalPolicy.ShouldAutoApprove] })` (builder = the `ChatClientBuilder` pipeline; verify exact overload from blog). Console handler: on approval request, print tool + args, read `y/n/a` (`a` = always-approve this tool → standing approval in session state).

- [ ] **Step 6: Run batch** — expect per-close approval prompts; answer `a` once, later closes auto-pass. Kill before finishing for Task 4.

- [ ] **Step 7: Commit** — `feat(p08): tool approval gate with standing approvals`

### Task 4: Kill and resume

**Files:**
- Modify: `src/P08.HarnessAgent/Program.cs`

- [ ] **Step 1: Persist session state** — serialize session between runs (method from session doc — e.g. session save/restore or export/import; harness blog references `/session-export` / `/session-import` in the UI surface). Store under `work/session-state/`.

- [ ] **Step 2: Run batch, Ctrl-C after 2nd ticket closes.** Confirm `work/session-state/` written + file memory has entries.

- [ ] **Step 3: Restart** — resume with same session; run same prompt. Expected: agent sees todos, skips finished tickets, continues to completion.

- [ ] **Step 4: Commit** — `feat(p08): session resume across restarts`

### Task 5: Notes + stretch

**Files:**
- Create: `docs/projects/08-harness-agent/NOTES.md`

- [ ] **Step 1: NOTES.md** — what `AsHarnessAgent` wired vs P05-P07 manual wiring (bullets: history persistence per model call inside tool loop, todo state, file memory path, approval state in session).
- [ ] **Step 2 (stretch):** plain `ChatClientAgent` + only `FileMemoryProvider` as context provider — observe what breaks without harness.
- [ ] **Step 3: Commit** — `docs(p08): learning notes`