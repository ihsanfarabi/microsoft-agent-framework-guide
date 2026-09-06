# Fix Code-Review Findings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all 10 verified findings from the 2026-09-02 whole-project deep code review (data loss, concurrency, error-contract, and parsing bugs).

**Architecture:** Fixes are scoped per finding, grouped by project so each task lands independently: MafDemo.Core persistence/retrieval hardening first (Tasks 1-3), then model-output parsing (Tasks 4-5), then the P13 hosted-service concurrency/contract cluster (Tasks 6-8), then P14/P15 honesty gaps (Tasks 9-10). No new files except tests; every fix follows the repo's established patterns (atomic tmp+Move writes, `.corrupt` quarantine, fail-soft startup, SSE error frames).

**Tech Stack:** .NET 10, C#, xUnit, ASP.NET Core minimal APIs, Microsoft.Extensions.AI.

**Spec:** The findings themselves — see `PORTFOLIO.md` and the per-project docs under `docs/projects/` for the documented contracts each fix restores. The 10 findings, in fix order:

| # | File | Finding |
|---|------|---------|
| F1 | `src/MafDemo.Core/Stores/FileTicketStore.cs:23` | `ToDictionary` dup-Id throws outside try, no quarantine |
| F2 | `src/MafDemo.Core/Stores/FileTicketStore.cs` (+ `P13.../TicketAgent.cs`) | Single-threaded store as DI singleton — races |
| F3 | `src/MafDemo.Core/Memory/FactMemoryStore.cs:147` | Non-atomic save; corrupt file crashes LoadAsync |
| F4 | `src/MafDemo.Core/Handbook/HandbookRetriever.cs:31` | No dim check, no score floor, BuildAsync duplicates |
| F5 | `src/P11.StructuredOutput/TypedTriage.cs:105` | Fence stripping only fires when response starts with ``` |
| F6 | `src/P14.SemanticMemory/Memory/FactExtractor.cs:72` | First-`[`-to-last-`]` slice corrupted by bracketed prose |
| F7 | `src/P13.StreamingApproval/Program.cs:297` | Shared AgentSession across concurrent requests, no lock |
| F8 | `src/P13.StreamingApproval/Program.cs:34` | Store/tombstone races (fixed via F2's locks) |
| F9 | `src/P13.StreamingApproval/Program.cs:76` | /messages SSE no catch-all — no error frame, checkpoint skipped |
| F10a | `src/P14.SemanticMemory/Program.cs:152` | RecallAsync extracts facts but never persists them |
| F10b | `src/P15.OrchestratorHost/Program.cs:159` | ExecutorFailedEvent never promoted — failed run can exit 0 |

## Global Constraints

- .NET 10 / C# preview conventions already in the repo: primary constructors, collection expressions (`[]`, `..`), file-scoped namespaces.
- No new NuGet packages.
- Repo convention: startup must survive corrupt persisted state — quarantine to `<path>.corrupt` (or `-.corrupt-<timestamp>` for P13 sessions), never delete the bad file silently.
- Atomic write pattern everywhere: serialize to `<path>.tmp`, then `File.Move(tmp, path, overwrite: true)`.
- Tests are offline — no Ollama, no network. Model-dependent tests gate on `RUN_EVALS=1`.
- Core doc comment says "Core is frozen for P13" for the store *contract* — adding internal locking does not change the `ITicketStore` surface, so it is allowed; do NOT add new public members to `ITicketStore`/`IDeletableTicketStore`.
- Commit message style: `fix(scope): summary` (see `git log`). No Co-Authored-By lines in this repo.
- Test runner: `dotnet test` per test project; full suite is `dotnet test MafDemo.slnx`.

---

### Task 1: FileTicketStore — duplicate-Id quarantine (F1)

**Files:**
- Modify: `src/MafDemo.Core/Stores/FileTicketStore.cs:17-34`
- Test: `tests/MafDemo.Core.Tests/FileTicketStoreTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: unchanged public surface (`FileTicketStore(string path)`). Later tasks rely on Load never throwing for any file content.

- [ ] **Step 1: Write the failing test**

Add to `tests/MafDemo.Core.Tests/FileTicketStoreTests.cs` (note: this file has no `namespace` declaration — match that):

```csharp
[Fact]
public async Task Duplicate_id_file_starts_empty_instead_of_throwing()
{
    var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
    var id = Guid.NewGuid();
    var dup = $$"""
        [
          {"Id":"{{id}}","Title":"a","Description":"d","Priority":0,"Status":0,"Notes":[]},
          {"Id":"{{id}}","Title":"b","Description":"d","Priority":0,"Status":0,"Notes":[]}
        ]
        """;
    File.WriteAllText(path, dup);

    var store = new FileTicketStore(path);        // must not throw (ArgumentException today)
    Assert.Empty(await store.ListAsync());
    // The unusable file is preserved for inspection, same as the corrupt case.
    Assert.True(File.Exists(path + ".corrupt"));
    File.Delete(path + ".corrupt");
}
```

(Verify the `Ticket` JSON property names against `src/MafDemo.Core/Domain/Ticket.cs` before running — the record's serialization names must match; if the record uses camelCase policy via attributes, adjust the test JSON accordingly. If unsure, build the duplicate content by serializing two `Ticket` records with the same Guid through `System.Text.Json` in the test instead of the literal.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj --filter Duplicate_id_file`
Expected: FAIL — `ArgumentException: An item with the same key has already been added` thrown from the `FileTicketStore` constructor.

- [ ] **Step 3: Implement — move `ToDictionary` inside the guarded region**

Replace `Load` in `src/MafDemo.Core/Stores/FileTicketStore.cs`:

```csharp
private static Dictionary<Guid, Ticket> Load(string p)
{
    if (!File.Exists(p)) return [];
    try
    {
        var list = JsonSerializer.Deserialize<List<Ticket>>(File.ReadAllText(p)) ?? [];
        return list.ToDictionary(t => t.Id);
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException)
    {
        // Corrupt or unusable file (a crash mid-write before atomic saves
        // existed, or a duplicate ticket Id from two app instances sharing
        // a work directory): preserve the bad data as <path>.corrupt so the
        // user can inspect it, but start empty rather than throwing from
        // the ctor and bricking every P04+ project that constructs this store.
        File.Move(p, p + ".corrupt", overwrite: true);
        return [];
    }
}
```

Also update the class doc comment line "corrupt file = moved to `.corrupt` and starts empty" to "corrupt or duplicate-id file = moved to `.corrupt` and starts empty" (line ~9).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj --filter Duplicate_id_file`
Expected: PASS

- [ ] **Step 5: Run the whole Core test project**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj`
Expected: PASS (all existing tests, including `Corrupt_file_starts_empty_instead_of_throwing`)

- [ ] **Step 6: Commit**

```bash
rtk git add src/MafDemo.Core/Stores/FileTicketStore.cs tests/MafDemo.Core.Tests/FileTicketStoreTests.cs
rtk git commit -m "fix(core): FileTicketStore quarantines duplicate-id file instead of throwing"
```

---

### Task 2: FileTicketStore + DeletableTicketStore — thread safety (F2/F8)

The P13 host registers these as DI singletons under a concurrent Kestrel server; the stores are documented "Single-threaded demo store — no locking". Rather than weakening P13's DI lifetimes (separate instances would cache independently and lose writes), make the stores actually thread-safe — the smallest change that makes the singleton registration honest.

**Files:**
- Modify: `src/MafDemo.Core/Stores/FileTicketStore.cs` (whole class body)
- Modify: `src/P13.StreamingApproval/Agents/TicketAgent.cs:179-238` (`DeletableTicketStore`)
- Test: `tests/MafDemo.Core.Tests/FileTicketStoreTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: unchanged public surface; all `FileTicketStore`/`DeletableTicketStore` members become safe for concurrent callers. P13's singleton registrations (Program.cs:34-35) stay as-is.

- [ ] **Step 1: Write the failing test**

Add to `tests/MafDemo.Core.Tests/FileTicketStoreTests.cs`:

```csharp
[Fact]
public async Task Concurrent_create_and_list_do_not_throw_or_lose_tickets()
{
    var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
    var store = new FileTicketStore(path);

    // Two racing writers on one singleton store: both must succeed, and a
    // concurrent reader must never observe a torn Dictionary (which throws
    // or, under a shared tmp file, IOException from File.Move).
    var tasks = Enumerable.Range(0, 8).Select(_ => store.CreateAsync("t", "d", TicketPriority.Normal));
    var created = await Task.WhenAll(tasks);
    var listed = await store.ListAsync();

    Assert.Equal(8, listed.Count);                        // nothing lost
    Assert.Equal(8, listed.Select(t => t.Id).Distinct().Count()); // no id collisions
    Assert.True(File.Exists(path));
    File.Delete(path);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj --filter Concurrent_create`
Expected: FAIL — race is timing-dependent; typical failure is `IOException` (shared `.tmp` path from two `File.WriteAllText` racing) or fewer than 8 tickets listed. If it happens to pass on a lucky run, rerun it ~5 times; the race reproduces reliably under `Task.WhenAll` fan-out.

- [ ] **Step 3: Implement — single gate lock in FileTicketStore**

In `src/MafDemo.Core/Stores/FileTicketStore.cs`, add a gate field and lock every member that touches `_tickets`, `_gate`, or `Save()`. Full replacement of the class body members (constructor + members; keep `Load` from Task 1 as-is):

```csharp
public class FileTicketStore(string path) : ITicketStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Ticket> _tickets = Load(path);

    // ... Load unchanged (Task 1) ...

    private void Save()
    {
        // Atomic write: serialize into a sibling temp file, then Move over the
        // real path. A crash mid-write leaves the previous file intact instead
        // of a truncated tickets.json that would brick the next startup.
        // Called only with _gate held, so concurrent writers never share the tmp path.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_tickets.Values.ToList(), Json));
        File.Move(tmp, path, overwrite: true);
    }

    public Task<Ticket> CreateAsync(string title, string description, TicketPriority priority)
    {
        Ticket t;
        lock (_gate)
        {
            t = new Ticket(Guid.NewGuid(), title, description, priority, TicketStatus.Open,
                null, DateTimeOffset.UtcNow, []);
            _tickets[t.Id] = t;
            Save();
        }
        return Task.FromResult(t);
    }

    public Task<Ticket?> GetAsync(Guid id)
    {
        lock (_gate)
        {
            return Task.FromResult(_tickets.GetValueOrDefault(id));
        }
    }

    public Task<IReadOnlyList<Ticket>> ListAsync()
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Ticket>>([.. _tickets.Values]);
        }
    }

    public Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(id, out var t)) return Task.FromResult<Ticket?>(null);
            t = t with { Status = status };
            _tickets[id] = t;
            Save();
            return Task.FromResult<Ticket?>(t);
        }
    }

    public Task<bool> AddNoteAsync(Guid id, string note)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(id, out var t)) return Task.FromResult(false);
            _tickets[id] = t with { Notes = [.. t.Notes, note] };
            Save();
            return Task.FromResult(true);
        }
    }
}
```

Update the class doc comment: replace "Single-threaded demo store — no locking." with "Safe for concurrent callers (single gate lock; the whole file rewrites under it — the demo's file IS the database, so writes serialize)."

- [ ] **Step 4: Implement — same gate in DeletableTicketStore**

In `src/P13.StreamingApproval/Agents/TicketAgent.cs`, `DeletableTicketStore` guards its unlocked `HashSet<Guid> _deleted` with its own gate (inner store already locks itself):

```csharp
public sealed class DeletableTicketStore : IDeletableTicketStore
{
    private readonly FileTicketStore _inner;
    private readonly string _deletedPath;
    private readonly object _gate = new();
    private HashSet<Guid> _deleted;
```

(keep the existing constructor, but change `_deleted = LoadDeleted(deletedPath);` — field can stay non-readonly since `LoadDeleted` is called in ctor; it is readonly-initialized so keep `readonly` only if the assignment stays in the ctor — it does, keep it.)

Members:

```csharp
    public async Task<bool> DeleteAsync(Guid id)
    {
        if (await _inner.GetAsync(id) is null) return false;
        lock (_gate)
        {
            if (_deleted.Contains(id)) return false;
            _deleted.Add(id);
            SaveDeleted();
        }
        return true;
    }

    public async Task<Ticket?> GetAsync(Guid id)
    {
        if (await _inner.GetAsync(id) is null) return null;
        lock (_gate)
        {
            if (_deleted.Contains(id)) return null;
        }
        return await _inner.GetAsync(id);
    }

    public async Task<IReadOnlyList<Ticket>> ListAsync()
    {
        Guid[] deletedSnapshot;
        lock (_gate)
        {
            deletedSnapshot = [.. _deleted];
        }
        var deletedSet = deletedSnapshot.ToHashSet();
        return (await _inner.ListAsync()).Where(t => !deletedSet.Contains(t.Id)).ToList();
    }
```

(`CreateAsync`/`UpdateStatusAsync`/`AddNoteAsync` pass through to `_inner`, which is now locked — leave unchanged. `LoadDeleted`/`SaveDeleted` are called only from ctor and inside `lock (_gate)` respectively — no further locking needed.)

Update the class doc comment to note it is safe for concurrent callers.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj`
Expected: PASS (concurrent test + all others)

- [ ] **Step 6: Run every project that consumes the stores**

Run: `dotnet test MafDemo.slnx`
Expected: PASS — P02/P05/P06/P08/P13 test projects all construct these stores.

- [ ] **Step 7: Commit**

```bash
rtk git add src/MafDemo.Core/Stores/FileTicketStore.cs src/P13.StreamingApproval/Agents/TicketAgent.cs tests/MafDemo.Core.Tests/FileTicketStoreTests.cs
rtk git commit -m "fix(core,p13): make ticket stores safe under the concurrent P13 host (gate locks)"
```

---

### Task 3: FactMemoryStore — atomic save + corrupt-file quarantine (F3)

**Files:**
- Modify: `src/MafDemo.Core/Memory/FactMemoryStore.cs:143-164`
- Test: `tests/MafDemo.Core.Tests/FactMemoryStoreTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: unchanged public surface. `SaveAsync` becomes crash-safe; `LoadAsync` becomes total (never throws for file content) and quarantines a corrupt file, matching `FileTicketStore`'s documented contract.

- [ ] **Step 1: Write the failing tests**

Add to `tests/MafDemo.Core.Tests/FactMemoryStoreTests.cs`:

```csharp
[Fact]
public async Task SaveAsync_leaves_no_tmp_file_and_survives_reload()
{
    var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
    var store = new FactMemoryStore(CreateEmbedder());
    await store.AddAsync("u1", EmailFact);

    await store.SaveAsync(path);

    Assert.False(File.Exists(path + ".tmp"));
    Assert.True(File.Exists(path));

    var reloaded = new FactMemoryStore(CreateEmbedder());
    await reloaded.LoadAsync(path);
    Assert.Single(await reloaded.ListAsync("u1"));
    File.Delete(path);
}

[Fact]
public async Task LoadAsync_corrupt_file_quarantines_and_starts_empty()
{
    var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
    File.WriteAllText(path, "{ truncated");   // e.g. a kill mid-save

    var store = new FactMemoryStore(CreateEmbedder());
    await store.LoadAsync(path);              // must not throw

    Assert.Empty(await store.ListAsync("u1"));
    Assert.True(File.Exists(path + ".corrupt")); // bad data preserved
    Assert.False(File.Exists(path));
    File.Delete(path + ".corrupt");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj --filter "SaveAsync_leaves_no_tmp|LoadAsync_corrupt"`
Expected: `SaveAsync_leaves_no_tmp_file_and_survives_reload` may pass (no crash mid-test); `LoadAsync_corrupt_file_quarantines_and_starts_empty` FAILs with `JsonException` from `DeserializeCollectionFromJsonAsync`.

- [ ] **Step 3: Implement — atomic tmp+Move save, quarantine on corrupt load**

Replace the save/load pair in `src/MafDemo.Core/Memory/FactMemoryStore.cs`:

```csharp
/// <summary>Serializes the fact collection to <paramref name="path"/> as JSON.
/// Atomic (tmp file + Move), so a crash mid-save leaves the previous file
/// intact instead of a truncated one that would brick the next startup —
/// the same discipline as FileTicketStore.</summary>
public async Task SaveAsync(string path)
{
    await _collection.EnsureCollectionExistsAsync();
    var tmp = path + ".tmp";
    using (var stream = File.Create(tmp))
    {
        await _vectorStore.SerializeCollectionAsJsonAsync<string, MemoryFact>(CollectionName, stream);
    }
    File.Move(tmp, path, overwrite: true);
}

/// <summary>
/// Loads a previously saved collection from <paramref name="path"/>. A
/// missing file is treated as an empty store (no-op); a corrupt or
/// unreadable file is moved to <c>&lt;path&gt;.corrupt</c> (preserved for
/// inspection) and the store starts empty — startup must survive corrupt
/// persisted state (P08 convention).
/// </summary>
public async Task LoadAsync(string path)
{
    if (!File.Exists(path))
    {
        return;
    }

    try
    {
        using var stream = File.OpenRead(path);
        await _vectorStore.DeserializeCollectionFromJsonAsync<string, MemoryFact>(stream);
    }
    catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
    {
        File.Move(path, path + ".corrupt", overwrite: true);
    }
}
```

Add `using System.Text.Json;` to the file's usings if not present (`JsonException`).

Then update `src/P14.SemanticMemory/Memory/FactStoreStartup.cs`'s class doc comment to reflect the new contract: `LoadAsync` is now fail-soft for a corrupt file too (it quarantines and starts empty, returning normally) — `TryLoadAsync` stays as the belt-and-suspenders guard for unreadable files (permissions, moved-aside failures) but the "corrupt JSON throws there" sentence is stale. Rewrite the doc comment accordingly:

```csharp
/// <summary>
/// Startup helper for loading a persisted <see cref="MafDemo.Core.Memory.FactMemoryStore"/>
/// from its JSON file. Repo convention (established in P08): startup must survive
/// corrupt persisted state. <see cref="MafDemo.Core.Memory.FactMemoryStore.LoadAsync"/>
/// itself quarantines a corrupt file and starts empty; this helper is the outer
/// guard for anything else that can go wrong reading the file (permissions, IO
/// errors), degrading to an empty store instead of crashing the host.
/// </summary>
```

The same stale sentence lives in the P14 `Program.cs` comment at lines 62-65 ("LoadAsync itself is fail-soft only for a MISSING file (corrupt JSON throws there)") — update it to say LoadAsync now quarantines corrupt files itself and TryLoadAsync is the outer IO guard.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj`
Expected: PASS (both new tests + all existing, including `Save_then_Load_recalls_without_re_adding` and `LoadAsync_missing_file_starts_empty`)

- [ ] **Step 5: Run P14 tests**

Run: `dotnet test tests/P14.SemanticMemory.Tests/P14.SemanticMemory.Tests.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
rtk git add src/MafDemo.Core/Memory/FactMemoryStore.cs src/P14.SemanticMemory/Memory/FactStoreStartup.cs src/P14.SemanticMemory/Program.cs tests/MafDemo.Core.Tests/FactMemoryStoreTests.cs
rtk git commit -m "fix(core): FactMemoryStore atomic save + corrupt-file quarantine"
```

---

### Task 4: HandbookRetriever — dimension check, relevance floor, rebuild dedupe (F4)

**Files:**
- Modify: `src/MafDemo.Core/Handbook/HandbookRetriever.cs` (whole file)
- Test: `tests/MafDemo.Core.Tests/HandbookRetrieverTests.cs`

**Interfaces:**
- Consumes: `IEmbedder` (unchanged), `HandbookChunk` (unchanged).
- Produces: `Task BuildAsync(IReadOnlyList<HandbookChunk> chunks)` — now idempotent (clears before appending). `Task<IReadOnlyList<HandbookChunk>> SearchAsync(string query, int topK = 3, float minScore = DefaultMinScore)` — NEW optional `minScore` parameter, default `DefaultMinScore = 0.3f`. Callers using the old two-arg form compile unchanged but now get the floor. A dimension mismatch now throws `InvalidOperationException` with a clear message instead of `IndexOutOfRangeException`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/MafDemo.Core.Tests/HandbookRetrieverTests.cs`. Two new embedder fakes:

```csharp
public class MismatchedDimsEmbedder : IEmbedder   // 32-dim, differs from KeywordEmbedder's 64
{
    public Task<float[]> EmbedAsync(string text)
    {
        var v = new float[32];
        v[text.Length % 32] = 1f;
        return Task.FromResult(v);
    }
}

/// <summary>Flips dimension between calls — builds 64-dim chunks, then
/// answers queries in 32 dims: the OLLAMA_EMBEDDING_MODEL switch, in miniature.</summary>
public class SwitchingEmbedder : IEmbedder
{
    public bool UseSmall { get; set; }
    public Task<float[]> EmbedAsync(string text)
    {
        var v = new float[UseSmall ? 32 : 64];
        v[text.Length % (UseSmall ? 32 : 64)] = 1f;
        return Task.FromResult(v);
    }
}
```

Tests:

```csharp
[Fact]
public async Task BuildAsync_called_twice_does_not_duplicate_entries()
{
    var r = new HandbookRetriever(new KeywordEmbedder());
    await r.BuildAsync(Chunks);
    await r.BuildAsync(Chunks);        // rebuild — same corpus, fresh process pattern

    var hits = await r.SearchAsync("how many vacation days do I get?");
    Assert.Equal(1, hits.Count);       // would be 2 (duplicate chunk) without the clear
    Assert.Equal("onboarding.md", hits[0].Doc);
}

[Fact]
public async Task SearchAsync_dimension_mismatch_throws_clear_error()
{
    var r = new HandbookRetriever(new KeywordEmbedder());
    await r.BuildAsync(Chunks);
    var mismatched = new HandbookRetriever(new MismatchedDimsEmbedder());
    await mismatched.BuildAsync(Chunks);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
        () => r.QueryWithOtherEmbedder());   // helper below, or inline:
}
```

Dimension mismatch needs the QUERY to differ from the CHUNKS' build dims within one retriever. Inline it:

```csharp
[Fact]
public async Task SearchAsync_dimension_mismatch_throws_clear_error()
{
    // Built 64-dim, queried 32-dim — the OLLAMA_EMBEDDING_MODEL switch class.
    var embedder = new SwitchingEmbedder();
    var r = new HandbookRetriever(embedder);
    await r.BuildAsync(Chunks);                  // UseSmall = false: 64-dim chunks

    embedder.UseSmall = true;                    // queries now come back 32-dim
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => r.SearchAsync("vpn"));
    Assert.Contains("dimension", ex.Message);
}

```
[Fact]
public async Task SearchAsync_unrelated_query_returns_nothing()
{
    var r = new HandbookRetriever(new KeywordEmbedder());
    await r.BuildAsync(Chunks);
    // A query sharing no characters with any chunk scores 0 everywhere —
    // below the floor, so the caller's "no handbook match" branch is reachable.
    var hits = await r.SearchAsync("zzz?? qqq?? www??");
    Assert.Empty(hits);
}

[Fact]
public async Task SearchAsync_zero_floor_returns_topK_regardless_of_score()
{
    var r = new HandbookRetriever(new KeywordEmbedder());
    await r.BuildAsync(Chunks);
    Assert.Equal(2, (await r.SearchAsync("backups", topK: 2, minScore: 0f)).Count);
}
```

Update the existing `Search_respects_topK` test to pass `minScore: 0f` (its "backups" 2nd-place chunk may fall below the new default floor):

```csharp
[Fact]
public async Task Search_respects_topK()
{
    var r = new HandbookRetriever(new KeywordEmbedder());
    await r.BuildAsync(Chunks);
    Assert.Equal(2, (await r.SearchAsync("backups", topK: 2, minScore: 0f)).Count);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj --filter HandbookRetriever`
Expected: FAIL — `BuildAsync_called_twice...` (2 hits, want 1), `SearchAsync_dimension_mismatch...` (compile error: no `minScore` param / no `InvalidOperationException`), `SearchAsync_unrelated_query...` (returns 3, want 0).

- [ ] **Step 3: Implement**

Replace `src/MafDemo.Core/Handbook/HandbookRetriever.cs` body:

```csharp
namespace MafDemo.Core.Handbook;

/// <summary>Minimum cosine similarity for a chunk to count as a match.
/// Below it the caller's "no handbook match" branch runs — without a floor,
/// zero-score chunks were returned as if they matched.</summary>
public class HandbookRetriever(IEmbedder embedder)
{
    public const float DefaultMinScore = 0.3f;

    private readonly List<(float[] Vector, HandbookChunk Chunk)> _entries = [];

    /// <summary>(Re)builds the index from <paramref name="chunks"/> — clears
    /// first, so a rebuild (fresh corpus, same process) never duplicates
    /// entries.</summary>
    public async Task BuildAsync(IReadOnlyList<HandbookChunk> chunks)
    {
        _entries.Clear();
        var vectors = await Task.WhenAll(chunks.Select(c => embedder.EmbedAsync(c.Text)));
        for (var i = 0; i < chunks.Count; i++)
            _entries.Add((vectors[i], chunks[i]));
    }

    /// <summary>Returns the up to <paramref name="topK"/> chunks scoring at
    /// least <paramref name="minScore"/> against the query, best first.
    /// Pass <c>minScore: 0f</c> for the old always-topK behavior.</summary>
    public async Task<IReadOnlyList<HandbookChunk>> SearchAsync(
        string query, int topK = 3, float minScore = DefaultMinScore)
    {
        var queryVector = await embedder.EmbedAsync(query);
        return _entries
            .Select((e, i) => (Score: Cosine(queryVector, e.Vector), Index: i, e.Chunk))
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(topK)
            .Select(x => x.Chunk)
            .ToList();
    }

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            // The one failure mode a dimension check exists for: the embedder
            // changed (e.g. OLLAMA_EMBEDDING_MODEL switched to a different
            // model) between build and query. A bare IndexOutOfRangeException
            // from the loop below would say nothing about the actual cause.
            throw new InvalidOperationException(
                $"embedding dimension mismatch: query has {a.Length} dims, chunk has {b.Length} — " +
                "was the embedding model changed after BuildAsync? Rebuild the retriever.");
        }

        var dot = 0f;
        var normA = 0f;
        var normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0f || normB == 0f) return 0f;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/MafDemo.Core.Tests/MafDemo.Core.Tests.csproj --filter HandbookRetriever`
Expected: PASS (all five)

- [ ] **Step 5: Check downstream callers compile and their tests pass**

The floor changes behavior for P04/P06/P07/P09/P10/P12 RAG paths (e.g. `SpecialistTools`' "(no handbook excerpts matched)" branch is now reachable). No code changes are needed there — but their tests must still pass:

Run: `dotnet test MafDemo.slnx`
Expected: PASS. If a downstream test asserted exact result counts on sub-floor matches, pass `minScore: 0f` at that call site ONLY if the test genuinely wants raw topK; otherwise the test was asserting the old always-return bug and should be updated to expect the filtered behavior.

- [ ] **Step 6: Commit**

```bash
rtk git add src/MafDemo.Core/Handbook/HandbookRetriever.cs tests/MafDemo.Core.Tests/HandbookRetrieverTests.cs
rtk git commit -m "fix(core): HandbookRetriever dim check, relevance floor, idempotent rebuild"
```

---

### Task 5: P11 — strip fences found anywhere in the response (F5)

**Files:**
- Modify: `src/P11.StructuredOutput/TypedTriage.cs:102-117` (`NormalizeJsonText`), `:124-153` (`JsonFenceCoercionClient`)
- Test: `tests/P11.StructuredOutput.Tests/TypedTriageTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `NormalizeJsonText(string)` — same signature, new behavior: a fence preceded by prose ("Sure! Here it is:\n```json\n{...}\n```") is stripped too. Also fixes `ComplianceFallback`'s probe implicitly (it reuses `NormalizeJsonText`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/P11.StructuredOutput.Tests/TypedTriageTests.cs`:

```csharp
[Fact]
public void ProbeJson_strips_fence_even_when_prose_precedes_it()
{
    var probe = TypedTriage.ProbeJson(
        "Sure! Here is the classification:\n```json\n" +
        """{"Category":"Hardware","Priority":"High","Summary":"dead battery"}""" +
        "\n```");
    Assert.True(probe.Ok);
}

[Fact]
public void NormalizeJsonText_plain_text_passes_through_unchanged()
{
    const string plain = """{"Category":"Network","Priority":1,"Summary":"wifi down"}""";
    Assert.Equal(plain, TypedTriage.NormalizeJsonText(plain));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/P11.StructuredOutput.Tests/P11.StructuredOutput.Tests.csproj --filter Fence`
Expected: `ProbeJson_strips_fence_even_when_prose_precedes_it` FAILs (probe reports not-Ok — the leading prose reaches the deserializer).

- [ ] **Step 3: Implement**

Replace `NormalizeJsonText` in `src/P11.StructuredOutput/TypedTriage.cs`:

```csharp
/// <summary>
/// Removes a markdown code fence (``` / ```json) and its closing fence
/// wherever they occur in the response, keeping only the body — which
/// <see cref="AgentResponse{T}.Result"/>'s deserializer can read. Cloud-routed
/// models often prepend a sentence ("Sure! Here is the classification:") before
/// the fence, so gating on "response starts with ```" leaves exactly those
/// cases broken. Text with no fence passes through unchanged.
/// </summary>
public static string NormalizeJsonText(string text)
{
    var trimmed = text.Trim();
    int open = trimmed.IndexOf("```", StringComparison.Ordinal);
    if (open < 0)
        return text;

    // The fence may be "```json\n" — the body starts after the fence's line.
    int firstNewline = trimmed.IndexOf('\n', open);
    if (firstNewline < 0)
        return text;

    var body = trimmed[(firstNewline + 1)..];
    int closing = body.LastIndexOf("```", StringComparison.Ordinal);
    if (closing >= 0)
        body = body[..closing];
    return body;
}
```

In `JsonFenceCoercionClient.GetResponseAsync` (same file, line ~130), loosen both gates from "starts with" to "contains":

```csharp
var response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
if (response.Text is { } text && text.Contains("```", StringComparison.Ordinal))
{
    foreach (var message in response.Messages)
    {
        if (!message.Text.Contains("```", StringComparison.Ordinal))
            continue;
        message.Contents =
        [
            new TextContent(NormalizeJsonText(message.Text)),
            .. message.Contents.Where(c => c is not TextContent),
        ];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/P11.StructuredOutput.Tests/P11.StructuredOutput.Tests.csproj`
Expected: PASS (new tests + `ProbeJson_accepts_valid_decision`, `ProbeJson_rejects_malformed_and_out_of_range`, `ComplianceFallbackTests` — the fallback's probe now also tolerates prose-prefixed fences, so if a `ComplianceFallbackTests` case asserts not-Ok for prose+fence input, that case was asserting the blind spot: update it to expect Ok.)

- [ ] **Step 5: Commit**

```bash
rtk git add src/P11.StructuredOutput/TypedTriage.cs tests/P11.StructuredOutput.Tests/TypedTriageTests.cs
rtk git commit -m "fix(p11): strip JSON fences preceded by prose, not only at response start"
```

---

### Task 6: P14 — ParseFacts survives bracketed prose (F6)

**Files:**
- Modify: `src/P14.SemanticMemory/Memory/FactExtractor.cs:65-90` (`ParseFacts`)
- Test: `tests/P14.SemanticMemory.Tests/UserMemoryProviderTests.cs` (the existing home of `ParseFacts` coverage — check; if `ParseFacts` tests live in a different file there, put these beside them)

**Interfaces:**
- Consumes: nothing new.
- Produces: `ChatClientFactExtractor.ParseFacts(string?)` — same signature; bracketed prose around the array no longer corrupts the slice; still never throws.

- [ ] **Step 1: Write the failing tests**

Add to `tests/P14.SemanticMemory.Tests/UserMemoryProviderTests.cs` (verify the file's namespace/usings first; these tests need `using P14.SemanticMemory.Memory;`):

```csharp
[Fact]
public void ParseFacts_ignores_brackets_in_prose_before_the_array()
{
    var facts = ChatClientFactExtractor.ParseFacts("""Okay [1 fact]: ["User prefers Slack over email"]""");
    var fact = Assert.Single(facts);
    Assert.Equal("User prefers Slack over email", fact);
}

[Fact]
public void ParseFacts_ignores_bracketed_remark_after_the_array()
{
    var facts = ChatClientFactExtractor.ParseFacts("""["User prefers email"] [end]""");
    var fact = Assert.Single(facts);
    Assert.Equal("User prefers email", fact);
}

[Fact]
public void ParseFacts_plain_array_still_parses()
{
    var facts = ChatClientFactExtractor.ParseFacts("""["User works night shifts"]""");
    Assert.Equal("User works night shifts", Assert.Single(facts));
}

[Fact]
public void ParseFacts_no_array_yields_empty()
{
    Assert.Empty(ChatClientFactExtractor.ParseFacts("no facts worth remembering"));
    Assert.Empty(ChatClientFactExtractor.ParseFacts(""));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/P14.SemanticMemory.Tests/P14.SemanticMemory.Tests.csproj --filter ParseFacts`
Expected: the first two FAIL (empty results today); the last two PASS (baseline).

- [ ] **Step 3: Implement**

Replace `ParseFacts` in `src/P14.SemanticMemory/Memory/FactExtractor.cs`:

```csharp
/// <summary>
/// Tolerant parser for the extractor's reply: finds the innermost slice that
/// deserializes as a JSON string array — prose brackets ("Okay [1 fact]: …",
/// "… [end]") no longer corrupt the slice, because candidate [ ... ] spans
/// are tried from the most-plausible (last opener with the last closer)
/// outward until one parses. Malformed output, non-string items, or an empty
/// response all yield an empty list — never an exception.
/// </summary>
public static IReadOnlyList<string> ParseFacts(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return [];
    }

    for (var end = text.LastIndexOf(']'); end > 0; end = text.LastIndexOf(']', end - 1))
    {
        for (var start = text.LastIndexOf('[', end); start >= 0; start = text.LastIndexOf('[', start - 1))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(text[start..(end + 1)]);
                if (parsed is null)
                {
                    return [];
                }

                var facts = parsed
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => f.Trim())
                    .ToArray();
                if (facts.Length > 0)
                {
                    return facts;
                }
            }
            catch (JsonException)
            {
                // This candidate span is not the array — try the next.
            }
        }
    }

    return [];
}
```

Behavior notes (these ARE the spec for the implementer): with input `["a"] [end]`, the outer loop first tries `end` = the `]` of `[end]`; the inner loop tries `[end]` (fails), then `["a"] [end]` (trailing content — `JsonException`), then exhausts; outer loop moves `end` to the `]` of `["a"]`, inner loop finds `["a"]` — parses, returns. Empty-but-valid arrays (`[]` parse to `Length == 0`) fall through to the next candidate and eventually return `[]`, same as today.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/P14.SemanticMemory.Tests/P14.SemanticMemory.Tests.csproj`
Expected: PASS (new + all existing `UserMemoryProviderTests`)

- [ ] **Step 5: Commit**

```bash
rtk git add src/P14.SemanticMemory/Memory/FactExtractor.cs tests/P14.SemanticMemory.Tests/UserMemoryProviderTests.cs
rtk git commit -m "fix(p14): ParseFacts survives brackets in prose around the JSON array"
```

---

### Task 7: P13 — per-conversation run serialization (F7)

Two concurrent requests on one conversation currently interleave `RunStreamingAsync` on the same `AgentSession` and race `CheckpointAsync` on the same tmp file. Fix: a per-conversation async gate, taken by BOTH endpoints around the whole run (get-or-create through checkpoint), so runs on the same conversation serialize while different conversations stay parallel.

**Files:**
- Modify: `src/P13.StreamingApproval/Program.cs` — `ConversationSessions` class (line ~280) and both endpoint handlers (lines ~59-90, ~105-204)
- Test: `tests/P13.StreamingApproval.Tests/SseContractTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ConversationSessions.AcquireAsync(string conversationId)` returning a `SemaphoreReleaser : IAsyncDisposable` — public shape:

```csharp
public async Task<SemaphoreReleaser> AcquireAsync(string conversationId)
public sealed record SemaphoreReleaser(SemaphoreSlim Gate) : IAsyncDisposable
```

- [ ] **Step 1: Write the failing test**

Add to `tests/P13.StreamingApproval.Tests/SseContractTests.cs`:

```csharp
[Fact]
public async Task Conversation_gate_serializes_concurrent_runs()
{
    using var factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient>(new ScriptedClient(
                ScriptedClient.TextThenList, ScriptedClient.FinalText));
        }));
    var client = factory.CreateClient();

    // Two double-submitted messages on ONE conversation: without a
    // per-conversation gate both runs execute against the same AgentSession
    // concurrently (interleaved history, racing checkpoints on one tmp file).
    // With the gate, the second run waits for the first to finish and both
    // complete — the checkpoint file left behind must be intact JSON.
    var conversationId = $"gate-{Guid.NewGuid():N}";
    var post = async () =>
    {
        using var content = new StringContent(
            """{"text":"just list them"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/conversations/{conversationId}/messages", content);
        return response.StatusCode;
    };

    var statuses = await Task.WhenAll(post(), post());

    Assert.All(statuses, s => Assert.Equal(System.Net.HttpStatusCode.OK, s));
    var work = Path.Combine(AppContext.BaseDirectory, "work", "sessions");
    var checkpoint = Path.Combine(work, $"{conversationId}.json");
    Assert.True(File.Exists(checkpoint));
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(checkpoint)); // intact, not torn
}
```

Note for the implementer: the WebApplicationFactory host runs the app out of the test's base directory, so both concurrent posts write the same sessions file — exactly the race. This test may pass by luck on the unfixed code; it is a regression guard for the fix. The deterministic assertion of the gate itself is the next test, which must fail before the fix:

```csharp
[Fact]
public async Task AcquireAsync_same_conversation_blocks_until_released()
{
    var sessions = new ConversationSessions(
        Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}"));

    await using var first = await sessions.AcquireAsync("c-lock");
    var second = sessions.AcquireAsync("c-lock");

    Assert.False(second.IsCompletedSuccessfully);      // still held
    await using var _ = await second;                    // would hang if never released —
}
```

That second test is awkward with a hang risk. Use the completion-with-timeout form instead:

```csharp
[Fact]
public async Task AcquireAsync_same_conversation_blocks_until_released()
{
    var sessions = new ConversationSessions(
        Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}"));

    await using var held = await sessions.AcquireAsync("c-lock");

    var acquire = sessions.AcquireAsync("c-lock");
    var winner = await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromSeconds(2)));

    Assert.NotSame(acquire, winner);                    // blocked: timeout won
    await using var releaser = await ReleaseAfter(acquire, held);

    async Task<ConversationSessions.SemaphoreReleaser> ReleaseAfter(
        Task<ConversationSessions.SemaphoreReleaser> pending, SemaphoreReleaser toRelease)
    {
        await toRelease.DisposeAsync();
        return await pending;
    }
}
```

(If the `SemaphoreReleaser` type name/type differs after implementation, adjust — the plan's Task-7 step 3 defines it as `SemaphoreReleaser`, a record implementing `IAsyncDisposable`. Use whatever signature step 3 landed, and simplify the test to use it directly if the nested helper reads badly.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/P13.StreamingApproval.Tests/P13.StreamingApproval.Tests.csproj --filter AcquireAsync`
Expected: compile error — `AcquireAsync` does not exist. (This is the "fails for the right reason" state: the API is the deliverable.)

- [ ] **Step 3: Implement**

Add to `ConversationSessions` in `src/P13.StreamingApproval/Program.cs` (inside the class):

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

/// <summary>Per-conversation run gate: both endpoints hold it for the whole
/// run (session get-or-create through checkpoint), so two runs on the SAME
/// conversation serialize — they share one AgentSession and one checkpoint
/// tmp file, and interleaving them duplicates/torn-writes both. Different
/// conversations hold different gates and stay parallel. Released via
/// DisposeAsync on the returned releaser.</summary>
public async Task<SemaphoreReleaser> AcquireAsync(string conversationId)
{
    var gate = _gates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync();
    return new SemaphoreReleaser(gate);
}

/// <summary>Releases one conversation gate on dispose — `await using` is the
/// intended usage.</summary>
public sealed record SemaphoreReleaser(SemaphoreSlim Gate) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        Gate.Release();
        return ValueTask.CompletedTask;
    }
}
```

Wire the `/messages` handler: after the blank-text validation, replace the session/stream/checkpoint block (Program.cs lines ~72-89) with:

```csharp
await using var _ = await sessions.AcquireAsync(id);
var session = await sessions.GetOrCreateAsync(id, agent);

http.Response.ContentType = "text/event-stream";
http.Response.Headers.CacheControl = "no-cache";
await StreamSseFramesAsync(http.Response,
    SseWriter.EnumerateFrames(
        agent.RunStreamingAsync(new ChatMessage(ChatRole.User, body.Text), session,
            cancellationToken: ct),
        id, session, approvals, ct),
    ct);
await sessions.CheckpointAsync(id, session, agent, ct);
return Results.Empty;
```

Wire the `/approvals` handler: acquire AFTER the TryTake + conversation-id check (the parked session belongs to this conversation; the gate must not be held while a bad/foreign request bounces), around the resume run (replace lines ~159-175's body):

```csharp
await using var _ = await sessions.AcquireAsync(conversationId);
http.Response.ContentType = "text/event-stream";
http.Response.Headers.CacheControl = "no-cache";
try
{
    await StreamSseFramesAsync(http.Response,
        SseWriter.EnumerateFrames(
            agent.RunStreamingAsync(resumeMessage, pending.Session, cancellationToken: ct),
            conversationId, pending.Session, approvals, ct),
        ct);
    await sessions.CheckpointAsync(conversationId, pending.Session, agent, ct);
}
// ... existing catch blocks unchanged (OperationCanceledException rethrow, catch (Exception) re-park + error frame) ...
```

Update the `ConversationSessions` class doc comment to document the gate ("per-conversation runs serialize; cross-conversation runs parallelize").

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/P13.StreamingApproval.Tests/P13.StreamingApproval.Tests.csproj`
Expected: PASS — both new tests + the full existing SSE contract suite (42K of tests covers both endpoints offline).

- [ ] **Step 5: Commit**

```bash
rtk git add src/P13.StreamingApproval/Program.cs tests/P13.StreamingApproval.Tests/SseContractTests.cs
rtk git commit -m "fix(p13): per-conversation gate serializes runs sharing one AgentSession"
```

---

### Task 8: P13 — /messages catch-all error frame + checkpoint (F9)

`StreamSseFramesAsync` only catches the approval-required `InvalidOperationException`; any other failure (model down = `HttpRequestException`) aborts the SSE connection with no error frame and skips the post-stream checkpoint — violating the endpoint's own "never a 500 / every terminal outcome is an SSE error frame" contract, which `/approvals` already honors with a `catch (Exception)` + error frame.

**Files:**
- Modify: `src/P13.StreamingApproval/Program.cs:59-90` (the `/messages` handler)
- Test: `tests/P13.StreamingApproval.Tests/SseContractTests.cs`

**Interfaces:**
- Consumes: `WriteSseErrorAsync` (already exists in Program.cs).
- Produces: no API change; behavior change only. `/messages` now ends every failure as an SSE `event: error` frame with `error = "run-failed"`.

- [ ] **Step 1: Write the failing test**

Add to `tests/P13.StreamingApproval.Tests/SseContractTests.cs`. The DI fake must throw mid-run — a minimal throwing client beside `ScriptedClient`:

```csharp
/// <summary>Chat client whose streaming call always fails — the model-down
/// shape (HttpRequestException) reaching the SSE endpoint mid-response.</summary>
public sealed class ThrowingClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new HttpRequestException("connection refused (fake)");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new HttpRequestException("connection refused (fake)");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
```

(Add `using System.Net.Http;` at the top if not already present.)

```csharp
[Fact]
public async Task Message_endpoint_model_failure_ends_as_sse_error_frame()
{
    // Model down mid-response: the stream has already started 200, so a 500
    // is impossible to deliver — the contract says the failure must arrive
    // as an `event: error` frame instead of a silently dropped connection.
    using var factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddSingleton<IChatClient>(new ThrowingClient())));
    var client = factory.CreateClient();

    var conversationId = $"err-{Guid.NewGuid():N}";
    using var content = new StringContent("""{"text":"anything"}""", Encoding.UTF8, "application/json");
    using var response = await client.PostAsync($"/conversations/{conversationId}/messages", content);

    var body = await response.Content.ReadAsStringAsync();
    Assert.Contains("event: error", body);
    Assert.Contains("run-failed", body);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/P13.StreamingApproval.Tests/P13.StreamingApproval.Tests.csproj --filter model_failure`
Expected: FAIL — body contains no `event: error` (the connection aborts after the 200 headers; `ReadAsStringAsync` may even throw or return a partial/empty body).

- [ ] **Step 3: Implement**

In the `/messages` handler (`src/P13.StreamingApproval/Program.cs`), wrap the stream + checkpoint (the block produced by Task 7) in a try/catch mirroring `/approvals`'s shape:

```csharp
await using var _ = await sessions.AcquireAsync(id);
var session = await sessions.GetOrCreateAsync(id, agent);

http.Response.ContentType = "text/event-stream";
http.Response.Headers.CacheControl = "no-cache";
try
{
    await StreamSseFramesAsync(http.Response,
        SseWriter.EnumerateFrames(
            agent.RunStreamingAsync(new ChatMessage(ChatRole.User, body.Text), session,
                cancellationToken: ct),
            id, session, approvals, ct),
        ct);
    // P08's checkpoint discipline: the moment the stream ends, the
    // session — history and any parked approval state — is serialized to
    // disk (atomic temp-file move), so a later message (or a process
    // restart) continues the conversation instead of starting over.
    await sessions.CheckpointAsync(id, session, agent, ct);
}
catch (OperationCanceledException)
{
    // Client disconnect or shutdown mid-run: nothing to deliver to — the
    // checkpoint is deliberately skipped (the next successful run's
    // checkpoint supersedes the stale file).
    throw;
}
catch (Exception ex)
{
    // Model down, tool crash, anything else mid-stream: the response has
    // already started 200 text/event-stream, so the only deliverable
    // failure shape is an SSE error frame — never a dropped connection
    // (the endpoint's own contract: every terminal outcome is an error
    // frame). The checkpoint is deliberately NOT taken when the run
    // threw: disk keeps the last known-good turn, same rule as
    // /approvals.
    await WriteSseErrorAsync(http.Response, new
    {
        error = "run-failed",
        detail = ex.Message,
        recovery = "re-send the message to retry the turn",
    }, ct);
}

return Results.Empty;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/P13.StreamingApproval.Tests/P13.StreamingApproval.Tests.csproj`
Expected: PASS — new test plus the whole existing suite (including `Continuation_of_paused_session_without_approval_emits_error_frame`, which still exercises the approval-required path inside `StreamSseFramesAsync`).

- [ ] **Step 5: Commit**

```bash
rtk git add src/P13.StreamingApproval/Program.cs tests/P13.StreamingApproval.Tests/SseContractTests.cs
rtk git commit -m "fix(p13): /messages streams every failure as an SSE error frame, not a dropped connection"
```

---

### Task 9: P14 — RecallAsync persists what the run learned (F10a)

`RecallAsync` runs the memory provider, which extracts and stores facts in memory — then exits without `SaveAsync`, so facts learned in recall mode die with the process (every other mode saves).

**Files:**
- Modify: `src/P14.SemanticMemory/Program.cs:107-108` (dispatch), `:152-159` (`RecallAsync`)

**Interfaces:**
- Consumes: nothing new.
- Produces: local-function signature change only: `static async Task RecallAsync(ChatClientAgent agent, FactMemoryStore factStore, string factsPath, string message)`. No public API.

- [ ] **Step 1: Change the call site and signature**

In `src/P14.SemanticMemory/Program.cs`, the dispatch (line ~108):

```csharp
case "recall":
    await RecallAsync(agent, factStore, factsPath, text.Length > 0 ? text : DefaultRecall);
    break;
```

And the local function (line ~152):

```csharp
static async Task RecallAsync(ChatClientAgent agent, FactMemoryStore factStore, string factsPath, string message)
{
    AgentSession session = await agent.CreateSessionAsync();
    Console.WriteLine("== session B (new) ==");
    Console.WriteLine($"user> {message}");
    var recall = await agent.RunAsync(message, session);
    Console.WriteLine($"bot> {recall.Text}");
    // The provider extracts and stores facts during EVERY run — recall
    // included. Persist them like every other mode (tell/repl/scripted
    // already save), or facts learned in recall mode die with the process.
    await factStore.SaveAsync(factsPath);
}
```

- [ ] **Step 2: Build (the behavior is model-backed — offline verification is compile + existing tests)**

Run: `dotnet build src/P14.SemanticMemory/P14.SemanticMemory.csproj`
Expected: build succeeds.

Run: `dotnet test tests/P14.SemanticMemory.Tests/P14.SemanticMemory.Tests.csproj`
Expected: PASS (no behavior change testable offline — `RecallAsync` is a local function in top-level `Program.cs`, which is not unit-testable by construction; the save call mirrors `TellAsync` line-for-line, and the store's save/load path is covered by Task 3's tests).

- [ ] **Step 3: Commit**

```bash
rtk git add src/P14.SemanticMemory/Program.cs
rtk git commit -m "fix(p14): recall mode persists facts extracted during the run"
```

---

### Task 10: P15 — promote ExecutorFailedEvent to a failed run (F10b)

`ExecutorFailedEvent` is only printed to stderr; if the engine surfaces a sub-executor failure in that form (rather than `WorkflowErrorEvent`), the orchestrator prints the route summary and returns 0 for a failed run — breaking the PROPAGATE contract that `demo15-failure.sh` asserts via exit codes.

**Files:**
- Modify: `src/P15.OrchestratorHost/Program.cs:130-194` (`RunScenarioAsync` event loop + post-loop failure block)

**Interfaces:**
- Consumes: `ExecutorFailedEvent` (Microsoft.Agents.AI.Workflows — already imported).
- Produces: no API change. Both terminal failure surfaces (`WorkflowErrorEvent`, `ExecutorFailedEvent`) now end in `WorkflowFailedException` → exit 1.

- [ ] **Step 1: Check ExecutorFailedEvent's actual shape first**

Run:

```bash
rtk grep -rn "class ExecutorFailedEvent" ~/.nuget/packages/microsoft.agents.ai.workflows* 2>/dev/null || \
dotnet build src/P15.OrchestratorHost/P15.OrchestratorHost.csproj && \
rtk grep -rn "ExecutorFailedEvent" src/P15.OrchestratorHost --include="*.cs"
```

Then inspect the type via metadata (e.g. `dotnet tool`-free approach: create the decompiled view with your IDE, or `strings` on the DLL, or check how `failure.Data` is already used at line 160 — `Console.Error.WriteLine($"[executor failed: {failure.ExecutorId}] {failure.Data}")`). The fix below assumes `failure.Data` is an `Exception` (the 1.19.0 event pairs `ExecutorId` with the failure payload); if metadata shows it is not, adapt the `original` extraction in step 2 to the real type — the invariant to preserve is: an observed `ExecutorFailedEvent` ends the run non-zero with the executor id named in the failure text.

- [ ] **Step 2: Implement**

In `RunScenarioAsync` (`src/P15.OrchestratorHost/Program.cs`), add a second failure capture beside `errorEvent` (line ~133):

```csharp
bool diagnosisHit = false;
bool inventoryHit = false;
WorkflowErrorEvent? errorEvent = null;
ExecutorFailedEvent? executorFailure = null;
```

Change the `ExecutorFailedEvent` case (line ~159):

```csharp
case ExecutorFailedEvent failure:
    // A sub-executor failure the engine surfaces per-executor rather than
    // as a whole-run WorkflowErrorEvent. Same PROPAGATE contract as below:
    // promoted to a failed run, never printed-and-swallowed — otherwise a
    // dead remote hop can print the route summary and exit 0.
    executorFailure = failure;
    Console.Error.WriteLine($"[executor failed: {failure.ExecutorId}] {failure.Data}");
    break;
```

After the loop, extend the post-loop failure block (before the existing `if (errorEvent is not null)` — or after it, order does not matter since both throw; put it AFTER so the existing WorkflowErrorEvent branch and its comment stay intact):

```csharp
if (errorEvent is null && executorFailure is not null)
{
    Exception original = executorFailure.Data as Exception
        ?? new InvalidOperationException(
            $"executor '{executorFailure.ExecutorId}' failed: {executorFailure.Data}");
    throw new WorkflowFailedException($"executor {executorFailure.ExecutorId} (per-executor failure event)", original);
}
```

- [ ] **Step 3: Build + offline verify**

Run: `dotnet build src/P15.OrchestratorHost/P15.OrchestratorHost.csproj`
Expected: build succeeds (P15 has no test project — the workflow engine's event shapes are the prerelease seam this project documents; the change is compile-verified and follows the existing PROPAGATE branch line-for-line).

Run: `dotnet test MafDemo.slnx`
Expected: PASS (no test regressions from an orchestrator-host change).

- [ ] **Step 4: Commit**

```bash
rtk git add src/P15.OrchestratorHost/Program.cs
rtk git commit -m "fix(p15): ExecutorFailedEvent propagates as a failed run instead of exiting 0"
```

---

## Final verification

- [ ] **Full suite:** `dotnet test MafDemo.slnx` — expect the pre-fix count (111) plus the new tests, all green.
- [ ] **Build:** `dotnet build MafDemo.slnx` — zero warnings introduced (fix any nullable/async warnings the new code produces).
- [ ] **Live smoke (optional, needs Ollama):** `dotnet run --project src/P13.StreamingApproval` then the README's curl flow — approval + resume still work; kill Ollama mid-turn and confirm `/messages` now emits `event: error` with `run-failed`.
- [ ] **Docs:** the fixed behaviors contradict nothing in README/PORTFOLIO, but if any per-project doc under `docs/projects/` repeats the "single-threaded store — no locking" or "LoadAsync throws on corrupt" claims, update those sentences in the same commits.

## Findings deliberately NOT fixed in this plan (out of the "fix all 10" scope)

- P13 unbounded `PendingApprovals`/`ConversationSessions` growth (host-lifetime leak; design decision for a demo host — could cap or prune as a follow-up).
- `HandbookChunker` CRLF mangling (latent; checked-in corpus is LF).
- `DeletableTicketStore` tombstone bypass in `UpdateStatusAsync`/`AddNoteAsync` (documented passthrough behavior — changing it alters the P13 contract; flagged as follow-up).
- 6-project corpus-load duplication (cleanup refactor, `HandbookCorpus.LoadChunks()` — separate cleanup plan, touches 6 projects).