# P11 StructuredOutput Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Typed JSON agent responses (`RunAsync<T>`) in a new console project, with the Ollama-Cloud silent-schema-drop handled (and taught) via a fallback path.

**Architecture:** Console project `P11.StructuredOutput` on the shared `OllamaChat.Create()` factory. Defines `TriageDecision`/`TicketDraft` records and drives one `ChatClientAgent` through the three MAF structured-output paths; a `ComplianceProbe` helper asserts schema compliance so the cloud gap is demonstrated, not hidden.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` 1.19.0 (inst. XML verified), OllamaSharp (`OllamaApiClient`), xUnit.

**Spec:** `docs/projects/11-structured-output/SPEC.md`

## Global Constraints

- .NET 10, C#, package line `Microsoft.Agents.AI*` 1.19.0 (-preview/-rc); Ollama endpoint `http://localhost:11434`, model default `glm-5.3-flash:cloud`.
- Live-model tests gate on `RUN_EVALS=1` via early-return `Fact` (repo convention; no SkippableFact).
- Zero edits to `src/P01`–`src/P10`; `MafDemo.Core` types consumed, not modified.
- All commits: `rtk git add -A && rtk git commit`, message convention `type(pNN): ...` ending `Co-Authored-By: Claude <noreply@anthropic.com>`.

---

### Task 1: Project scaffold + typed `RunAsync<TriageDecision>`

**Files:**
- Create: `src/P11.StructuredOutput/P11.StructuredOutput.csproj`, `src/P11.StructuredOutput/TypedTriage.cs`, `src/P11.StructuredOutput/Program.cs`, `src/P11.StructuredOutput/appsettings.json`
- Test: `tests/P11.StructuredOutput.Tests/TypedTriageTests.cs` (new project)

**Interfaces:**
- Consumes: `MafDemo.AgentCommon.OllamaChat.Create()`; `MafDemo.Core.Domain.TicketPriority { Low, Normal, High, Critical }`.
- Produces: `record TriageDecision(TicketCategory Category, TicketPriority Priority, string Summary)` with `enum TicketCategory { Hardware, Network, Account, Security, Other }`; `static ChatClientAgent TypedTriageAgent(IChatClient client)`; `record ComplianceProbe(bool Ok, string Raw)` + `static ComplianceProbe ProbeJson(string text)`.

- [ ] **Step 1: scaffold project** — csproj (net10.0, refs `MafDemo.AgentCommon` + `MafDemo.Core`, packages copied from `src/P02.TicketTools/P02.TicketTools.csproj` pattern: `Microsoft.Agents.AI` 1.19.0 line), `appsettings.json` `{"Ollama":{"Endpoint":"http://localhost:11434","Model":"glm-5.3-flash:cloud"}}`. Add test project to `MafDemo.slnx`.

- [ ] **Step 2: write failing test for the probe**

```csharp
[Fact]
public void ProbeJson_accepts_valid_decision()
{
    var probe = P11.StructuredOutput.TypedTriage.ProbeJson(
        """{"Category":"Hardware","Priority":2,"Summary":"dead battery"}""");
    Assert.True(probe.Ok);
}
```
(`ProbeJson` returns `ComplianceProbe` with `bool Ok` + `string Error` — adjust names when writing both together; keep test and helper in one compile unit.)

- [ ] **Step 3: run** `rtk dotnet test tests/P11.StructuredOutput.Tests` → expected FAIL (type missing).

- [ ] **Step 4: implement `TypedTriage.cs`** — `TriageDecision` record; `ProbeJson`: try `JsonSerializer.Deserialize<TriageDecision>(text, Options)` (case-insensitive, enum-reader tolerant like P08's), return `Ok=true/false`. `TypedTriageAgent`: `new ChatClientAgent(chatClient, new ChatClientAgentOptions { ChatOptions = new() { Name = "TriageBot", Instructions = "Classify the user's ticket. Respond with JSON only." } })`.

- [ ] **Step 5: test green**, then live smoke (gated):

```csharp
[Fact]
public async Task RunAsync_returns_typed_decision()
{
    if (Environment.GetEnvironmentVariable("RUN_EVALS") != "1") return;
    var agent = TypedTriageAgent(OllamaChat.Create());
    var response = await agent.RunAsync<TriageDecision>("Laptop won't boot, deadline tomorrow");
    Assert.NotEqual(TicketCategory.Undefined, response.Result.Category); // any category; null-check type
}
```

Run `RUN_EVALS=1 rtk dotnet test tests/P11.StructuredOutput.Tests` → PASS. If `RunAsync<T>` differs from the spec's signature, consult the installed XML doc (`~/.nuget/packages/microsoft.agents.ai/1.19.0/lib/net10.0/Microsoft.Agents.AI.xml`) and record the divergence.

- [ ] **Step 6: commit** `feat(p11): typed RunAsync<T> classifier baseline`.

### Task 2: The three format paths

**Files:** Modify `src/P11.StructuredOutput/Program.cs`, `src/P11.StructuredOutput/TypedTriage.cs`.

**Interfaces:** Consumes Task 1 records. Produces: `static Task<AgentResponse<TicketDraft>> ExtractRawAsync(...)`-style demo methods (exact signatures decided in-file, referenced by Program only).

- [ ] **Step 1: typed path** — `await agent.RunAsync<TriageDecision>(message)`.
- [ ] **Step 2: per-call options path** — `new ChatClientAgentRunOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema<TriageDecision>() }` on `RunAsync<T>`.
- [ ] **Step 3: raw path** — `ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(JsonSchema.For<TicketDraft>()), "TicketDraft", "...")` or hand-written `JsonElement`; print `response.Text`, `JsonSerializer.Deserialize<TicketDraft>(response.Text)`. If `ForJsonSchema<T>()` throws on this model/package combo at runtime, capture as NOTES divergence.
- [ ] **Step 4: Program.cs prints all three results side by side.** Run once live (`dotnet run --project src/P11.StructuredOutput`). Commit: `feat(p11): three response-format paths`.

### Task 3: Cloud-reality fallback

**Files:** Create `src/P11.StructuredOutput/ComplianceFallback.cs`; Modify `Program.cs`.

**Interfaces:** Produces `static async Task<T?> RunJsonWithFallbackAsync<T>(AIAgent agent, string message, ChatClientAgentRunOptions opts)` — runs with `ResponseFormat`, `ProbeJson`s, re-prompts once embedding the JSON schema in the prompt, tolerant-parses (strip code fences) — else returns default.

- [ ] **Step 1: failing test** — `RunJsonWithFallbackAsync` given a stub `IChatClient` (mock returning prose around JSON, e.g. ```json fences) still yields typed result. Assert via fake `IChatClient` that returns fenced JSON.
- [ ] **Step 2: implement**, test green.
- [ ] **Step 3: demo honestly** — console prints "schema enforced=false (ollama cloud ignored schema — ollama#12362)" vs `OLLAMA_MODEL=<local> OLLAMA_ENDPOINT=… dotnet run` note showing enforcement when a local schema-capable model is set. Confirm in NOTES.
- [ ] **Step 4: commit** `feat(p11): schema-compliance probe + embedded-schema fallback`.

### Task 4: Docs + portfolio

**Files:** Create `docs/projects/11-structured-output/NOTES.md`; Modify `PORTFOLIO.md`, `README.md`.

- [ ] **Step 1: NOTES.md** — divergences found at runtime (`RunAsync<T>` signature truth vs doc, cloud schema drop behavior on this exact model string, fallback decorator decision), what worked.
- [ ] **Step 2: README ladder row + PORTFOLIO table row** for P11; keep tables consistent.
- [ ] **Step 3:** `rtk dotnet test MafDemo.slnx` green; commit `docs(p11): notes + portfolio entries`.