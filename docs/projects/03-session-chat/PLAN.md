# P03: SessionChat — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** REPL chat whose conversation and tickets survive process restarts.

**Architecture:** `FileTicketStore` added to Core (TDD). P03 REPL keeps one `AgentSession` per conversation, serializes it to `threads/<id>.json` after each turn, deserializes on `/switch`. Verified API: `agent.CreateSessionAsync()`, `agent.RunAsync(prompt, session)`, `agent.SerializeSession(session)` → serializable object, `agent.DeserializeSessionAsync(serialized)`.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` (prerelease), `OllamaSharp`, OpenTelemetry, xUnit.

**Spec:** `docs/projects/03-session-chat/SPEC.md`

## Global Constraints

- Model: `glm-5.3-flash:cloud` via Ollama; tool pipeline needs `ChatClientBuilder(...).UseFunctionInvocation().Build()` (P02 pattern).
- Session API verified against https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/session — if serialization shape differs in a newer package version, copy from the doc sample.
- `threads/` and `tickets.json` are runtime state — gitignored.
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: FileTicketStore (TDD)

**Files:**
- Create: `src/MafDemo.Core/Stores/FileTicketStore.cs`
- Test: `tests/MafDemo.Core.Tests/FileTicketStoreTests.cs`

**Interfaces:**
- Produces: `class FileTicketStore(string path) : ITicketStore` — JSON-file-backed; persists after every mutation; loads existing file on construction (missing file = starts empty)
- Consumes: `Ticket`, `ITicketStore`, enums (P01)

- [x] **Step 1: Write failing tests**

```csharp
// tests/MafDemo.Core.Tests/FileTicketStoreTests.cs
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

public class FileTicketStoreTests
{
    [Fact]
    public async Task Create_persists_to_disk_and_roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileTicketStore(path);
        var created = await store.CreateAsync("VPN", "broken", TicketPriority.High);

        var reloaded = new FileTicketStore(path);      // fresh instance, same file
        var loaded = await reloaded.GetAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal("VPN", loaded!.Title);
        File.Delete(path);
    }

    [Fact]
    public async Task AddNote_survives_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileTicketStore(path);
        var t = await store.CreateAsync("t", "d", TicketPriority.Normal);
        await store.AddNoteAsync(t.Id, "restarted laptop");

        var reloaded = new FileTicketStore(path);
        var loaded = await reloaded.GetAsync(t.Id);
        Assert.Contains("restarted laptop", loaded!.Notes);
        File.Delete(path);
    }

    [Fact]
    public async Task Missing_file_starts_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileTicketStore(path);
        Assert.Empty(await store.ListAsync());
    }
}
```

- [x] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests/MafDemo.Core.Tests`
Expected: compile error — `FileTicketStore` missing.

- [x] **Step 3: Implement** — load `List<Ticket>` from JSON in ctor (missing file → empty list), keep in-memory dict, rewrite whole file after each mutation via `System.Text.Json`:

```csharp
// src/MafDemo.Core/Stores/FileTicketStore.cs
using System.Text.Json;
using MafDemo.Core.Domain;

namespace MafDemo.Core.Stores;

public class FileTicketStore(string path) : ITicketStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Dictionary<Guid, Ticket> _tickets = Load(path);

    private static Dictionary<Guid, Ticket> Load(string p)
    {
        if (!File.Exists(p)) return [];
        var list = JsonSerializer.Deserialize<List<Ticket>>(File.ReadAllText(p)) ?? [];
        return list.ToDictionary(t => t.Id);
    }

    private void Save() =>
        File.WriteAllText(path, JsonSerializer.Serialize(_tickets.Values.ToList(), Json));

    public async Task<Ticket> CreateAsync(string title, string description, TicketPriority priority)
    {
        var t = new Ticket(Guid.NewGuid(), title, description, priority, TicketStatus.Open,
            null, DateTimeOffset.UtcNow, []);
        _tickets[t.Id] = t; Save();
        return await Task.FromResult(t);
    }

    public Task<Ticket?> GetAsync(Guid id) =>
        Task.FromResult(_tickets.GetValueOrDefault(id));

    public Task<IReadOnlyList<Ticket>> ListAsync() =>
        Task.FromResult<IReadOnlyList<Ticket>>([.. _tickets.Values]);

    public Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status)
    {
        if (!_tickets.TryGetValue(id, out var t)) return Task.FromResult<Ticket?>(null);
        t = t with { Status = status };
        _tickets[id] = t; Save();
        return Task.FromResult<Ticket?>(t);
    }

    public Task AddNoteAsync(Guid id, string note)
    {
        if (_tickets.TryGetValue(id, out var t))
        {
            _tickets[id] = t with { Notes = [.. t.Notes, note] };
            Save();
        }
        return Task.CompletedTask;
    }
}
```

- [x] **Step 4: Run tests, verify PASS** — `dotnet test tests/MafDemo.Core.Tests`

- [x] **Step 5: Commit** — `feat(core): file-backed ticket store`

### Task 2: Session REPL

**Files:**
- Create: `src/P03.SessionChat/` console project (refs `MafDemo.Core`), `Program.cs`, `TicketBot.cs` (copy from P02 with `FileTicketStore`)
- Modify: `MafDemo.sln`

**Interfaces:**
- Consumes: `TicketToolFunctions` + agent factory pattern (P02), `FileTicketStore` (Task 1)
- Produces: REPL entry point

- [x] **Step 1: Scaffold** — `dotnet new console -n P03.SessionChat -o src/P03.SessionChat -f net10.0`; add to sln; add packages (P02 set); reference Core; copy `TicketToolFunctions.cs`, `TicketBot.cs` from P02; use `MafDemo.AgentCommon` for OTel wiring.

- [x] **Step 2: REPL with session** — verified session API:

```csharp
var store = new FileTicketStore("tickets.json");
var agent = TicketBot.Create(store);
AgentSession session = await agent.CreateSessionAsync();
var sessionId = Guid.NewGuid().ToString("N")[..8];

Console.WriteLine("commands: /new /list /switch <id> /quit");
while (true)
{
    Console.Write("you> ");
    var text = Console.ReadLine()?.Trim();
    if (text is null or "" or "/quit") break;
    if (text == "/new") { session = await agent.CreateSessionAsync(); sessionId = Guid.NewGuid().ToString("N")[..8]; Console.WriteLine($"new session {sessionId}"); continue; }
    if (text == "/list") { Console.WriteLine(string.Join("\n", Directory.GetFiles("threads").Select(Path.GetFileNameWithoutExtension))); continue; }
    if (text.StartsWith("/switch ")) { /* Task 3 */ continue; }

    Console.WriteLine($"bot> {await agent.RunAsync(text, session)}");
    SessionPersistence.Save(agent, session, sessionId);   // Task 3
}
```

- [x] **Step 3: Run and verify in-process memory** — "my laptop model is LTX-2201" → "create a ticket for it" → "what's my laptop model?" — answered without restating.

- [x] **Step 4: Commit** — `feat(p03): session REPL`

### Task 3: Session persistence + /switch

**Files:**
- Create: `src/P03.SessionChat/SessionPersistence.cs`
- Modify: `src/P03.SessionChat/Program.cs`

**Interfaces:**
- Produces: `static class SessionPersistence { void Save(ChatClientAgent agent, AgentSession session, string id); Task<AgentSession> Load(ChatClientAgent agent, string id); }`

- [x] **Step 1: Implement save/load** — verified serialization API from session doc:

```csharp
public static class SessionPersistence
{
    private static readonly string Dir = "threads";

    public static void Save(ChatClientAgent agent, AgentSession session, string id)
    {
        Directory.CreateDirectory(Dir);
        var serialized = agent.SerializeSession(session);   // per doc: sync serialize
        File.WriteAllText(Path.Combine(Dir, $"{id}.json"),
            System.Text.Json.JsonSerializer.Serialize(serialized));
    }

    public static async Task<AgentSession> Load(ChatClientAgent agent, string id)
    {
        var raw = System.Text.Json.JsonSerializer.Deserialize<object>(
            File.ReadAllText(Path.Combine(Dir, $"{id}.json")));
        return await agent.DeserializeSessionAsync(raw!);   // exact param type: check doc sample
    }
}
```

- [x] **Step 2: Wire `/switch <id>`** — `session = await SessionPersistence.Load(agent, id); sessionId = id;`

- [x] **Step 3: Restart verification** — chat ("my laptop model is LTX-2201", create ticket), `/quit`, rerun, `/list`, `/switch <id>`, ask "what's my laptop model?" and "what was my last ticket?".

Expected: both answered. **If history is lost** (session serialization carries only `StateBag` for local agents, not chat history — possible per docs), add a history provider step: capture conversation messages via a context provider (`StoreAIContextAsync` per P04 pattern) into the session's `StateBag`, and note the fallback in NOTES.md.

- [x] **Step 4: Verify ticket persistence** — `tickets.json` exists with the ticket from turn 1 after restart.

- [x] **Step 5: Commit** — `feat(p03): session persistence across restarts`

### Task 4: Wrap-up

**Files:**
- Create: `docs/projects/03-session-chat/NOTES.md`

- [x] **Step 1: NOTES.md** — bullets: session vs thread vs history provider (what actually held the memory after restart); what `StateBag` carried; where serialization boundary bit (if it did).
- [x] **Step 2 (stretch):** durable-fact context provider across different sessions (uses `AIContextProvider` + `ProviderSessionState` — P04 Task 3 pattern).
- [x] **Step 3: Commit** — `docs(p03): wrap-up notes`