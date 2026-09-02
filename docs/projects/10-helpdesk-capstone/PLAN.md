# P10: HelpDeskCapstone — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Self-hosted HelpDeskHQ app: protocol endpoints, YAML-declared agents, eval suite, CI, Docker Compose.

**Architecture:** One ASP.NET Core app resolving named agents via DI; protocol packages map endpoints; YAML files define FaqBot + triage router; xUnit eval harness gates quality in CI; compose wires app + Aspire dashboard + host Ollama.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI.Hosting` (+ OpenAI-compatible + A2A protocol packages), `Microsoft.Agents.AI.Declarative`, OllamaSharp, GitHub Actions, Docker Compose.

**Spec:** `docs/projects/10-helpdesk-capstone/SPEC.md`

## Global Constraints

- Verified: shared hosting via `Microsoft.Agents.AI.Hosting` (generic host + DI; protocol packages map named `AIAgent` instances to endpoints); A2A server via `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` + `app.MapA2A(agent, path)`; declarative agents via `Microsoft.Agents.AI.Declarative` — `ChatClientPromptAgentFactory` / `CreateFromYamlAsync` on `PromptAgentFactory`.
- OpenAI-compatible endpoint package name unverified — expected `Microsoft.Agents.AI.Hosting.OpenAI` (check self-hosting page before Task 1).
- YAML agents must produce same behavior as C# predecessors; spot-check outputs side by side.
- Evals hit the real Ollama model — run only when `RUN_EVALS=1` (CI sets it; local runs against live daemon).
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: App scaffold + chat endpoint

**Files:**
- Create: `src/P10.HelpDeskCapstone/` web project — `Program.cs`, `Agents/AgentsSetup.cs`, `Dockerfile`
- Modify: `MafDemo.sln`

- [x] **Step 1: Scaffold**

```bash
dotnet new web -n P10.HelpDeskCapstone -o src/P10.HelpDeskCapstone -f net10.0
dotnet sln add src/P10.HelpDeskCapstone
dotnet add src/P10.HelpDeskCapstone reference src/MafDemo.Core src/P04.HandbookRag
dotnet add src/P10.HelpDeskCapstone package Microsoft.Agents.AI.Hosting --prerelease
dotnet add src/P10.HelpDeskCapstone package Microsoft.Agents.AI.Hosting.OpenAI --prerelease  # name: verify on self-hosting page
dotnet add src/P10.HelpDeskCapstone package Microsoft.Agents.AI.Hosting.A2A.AspNetCore --prerelease
dotnet add src/P10.HelpDeskCapstone package Microsoft.Agents.AI.Declarative --prerelease
dotnet add src/P10.HelpDeskCapstone package OllamaSharp
```

- [x] **Step 2: Register agents via DI** (`AgentsSetup.cs`) — RAG agent from P04 over function-invoking Ollama client; verify the Responses/OpenAI-compatible mapping call from the self-hosting page sample (pattern: package resolves named `AIAgent` from DI and maps a Responses/Chat Completions route):

```csharp
builder.Services.AddSingleton(sp => HandbookRagFacts.Build()); // P04 factory
builder.AddAIAgent("helpdesk", (sp, _) => sp.GetRequiredService<ChatClientAgent>());
```

- [x] **Step 3: Map endpoints in `Program.cs`** — OpenAI-compatible chat endpoint for `"helpdesk"` (exact mapping method from self-hosting doc) + health endpoint.

- [x] **Step 4: Run + verify**

```bash
dotnet run --project src/P10.HelpDeskCapstone
curl -s http://localhost:5080/v1/chat/completions -H 'Content-Type: application/json' \
  -d '{"messages":[{"role":"user","content":"How do I reset my password?"}]}'
```
Expected: grounded handbook answer (route path per doc; adjust curl).

- [x] **Step 5: Commit** — `feat(p10): self-hosted app with chat endpoint`

### Task 2: Declarative YAML agents

**Files:**
- Create: `src/P10.HelpDeskCapstone/agents/faq.yaml`, `agents/triage.yaml`
- Create: `src/P10.HelpDeskCapstone/Agents/YamlAgents.cs`
- Modify: `Program.cs` — load at startup, register via DI

- [x] **Step 1: Write `agents/faq.yaml`** — schema from declarative agents doc (verified package provides `ChatClientPromptAgentFactory` + `CreateFromYamlAsync`):

```yaml
name: FaqBot
description: HelpDeskHQ FAQ bot
instructions: |
  You are HelpDeskHQ's FAQ bot. Answer IT questions in one short paragraph.
model:
  provider: ollama
  model: glm-5.3-flash:cloud
```
(Schema fields: copy exact keys from doc sample — do not invent.)

- [x] **Step 2: Write `agents/triage.yaml`** — triage router: instructions matching P06 triage (route to network/software/hardware), no tools.

- [x] **Step 3: Load in `YamlAgents.cs`**

```csharp
using Microsoft.Agents.AI.Declarative; // verify namespace from doc
public static class YamlAgents
{
    public static async Task<Dictionary<string, AIAgent>> LoadAllAsync(string dir, IChatClient client)
    {
        var factory = new ChatClientPromptAgentFactory(client); // verify ctor from doc sample
        var agents = new Dictionary<string, AIAgent>();
        foreach (var path in Directory.GetFiles(dir, "*.yaml"))
            agents[Path.GetFileNameWithoutExtension(path)] = await factory.CreateFromYamlAsync(File.ReadAllText(path));
        return agents;
    }
}
```

- [x] **Step 4: Register + map A2A for FaqBot** — `app.MapA2A(faqAgent, "/a2a/faq")`; verify card with curl.

- [x] **Step 5: Spot-check** — same prompt to YAML FaqBot (via A2A curl or test call) vs P01 FaqBot output; record in NOTES.md.

- [x] **Step 6: Commit** — `feat(p10): declarative yaml agents + a2a endpoint`

### Task 3: Eval harness (TDD)

**Files:**
- Create: `src/MafDemo.Core/Evals/EvalCase.cs` — `record EvalCase(string Input, string ExpectedFact)`
- Create: `tests/MafDemo.Core.Tests/EvalRunnerTests.cs` — runner logic against a fake agent
- Create: `tests/MafDemo.Core.Tests/EvalSuite.cs` — 8 real cases against RAG agent (gated)

**Interfaces:**
- Produces: `static class EvalRunner { public static async Task<EvalResult> RunAsync(IEnumerable<EvalCase> cases, Func<string, Task<string>> answer); public record EvalResult(int Passed, int Total, string[] Failures); }`

- [x] **Step 1: Write failing tests with fake agent**

```csharp
[Fact]
public async Task Pass_counted_and_failures_listed()
{
    Func<string, Task<string>> fake = q => Task.FromResult(q == "a" ? "VPN requires MFA" : "nope");
    var result = await EvalRunner.RunAsync(
        [new("a", "mfa"), new("b", "something")], fake);
    Assert.Equal((1, 2), (result.Passed, result.Total));
    Assert.Single(result.Failures);
}
```

- [x] **Step 2: Run, verify FAIL** — `dotnet test --filter EvalRunner`

- [x] **Step 3: Implement** — case-insensitive `answer.Contains(ExpectedFact, StringComparison.OrdinalIgnoreCase)`; collect failures with input echo.

- [x] **Step 4: Run, verify PASS**

- [x] **Step 5: Write `EvalSuite.cs`** — 8 cases grounded in corpus facts (password reset steps, VPN MFA, RMA window, Wi-Fi SSID…); `[SkippableFact]`-style gate: skip unless `RUN_EVALS=1`; print pass rate to console on run.

- [x] **Step 6: Run live** — `RUN_EVALS=1 dotnet test --filter EvalSuite`
Expected: ≥8/8 (retry flaky cases once; if a case fails on model behavior, sharpen the expected fact, not the corpus).

- [x] **Step 7: Commit** — `feat(p10): eval harness + 8-case suite`

### Task 4: CI

**Files:**
- Create: `.github/workflows/ci.yml`

- [x] **Step 1: Write workflow**

```yaml
name: ci
on: [push, pull_request]
jobs:
  build-test-eval:
    runs-on: ubuntu-latest
    env:
      RUN_EVALS: "1"
      OLLAMA_ENDPOINT: http://host.docker.internal:11434  # adjust to CI Ollama service
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet build MafDemo.sln
      - run: dotnet test MafDemo.sln --logger 'console;verbosity=normal'
```
(For evals in CI, add an Ollama service container or action installing Ollama + `ollama pull glm-5.3-flash:cloud`; if cloud model can't run in CI, keep eval job manual-dispatch only — decide in implementation and note it.)

- [x] **Step 2: Push + verify** — workflow green: build + tests + evals (or evals job documented as manual).

- [x] **Step 3: Commit** — `ci(p10): build test eval pipeline`

### Task 5: Docker Compose + portfolio docs

**Files:**
- Create: `docker-compose.yml`, `src/P10.HelpDeskCapstone/Dockerfile`
- Create: `PORTFOLIO.md`

- [x] **Step 1: Dockerfile** — multi-stage: `mcr.microsoft.com/dotnet/sdk:10.0` build → `aspnet:10.0` runtime; copy corpus + agents YAML.

- [x] **Step 2: docker-compose.yml**

```yaml
services:
  app:
    build: { context: ., dockerfile: src/P10.HelpDeskCapstone/Dockerfile }
    ports: ["5080:8080"]
    environment:
      OLLAMA_ENDPOINT: http://host.docker.internal:11434
      OTEL_EXPORTER_OTLP_ENDPOINT: http://dashboard:18889
  dashboard:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:latest
    ports: ["18888:18888"]
```
(Ports/env: verify dashboard OTLP receiver port from Aspire dashboard docs.)

- [x] **Step 3: Full startup test** — `docker compose up --build`; `curl` chat endpoint → grounded answer; traces visible in dashboard at `:18888`.

- [x] **Step 4: `PORTFOLIO.md`** — mermaid architecture diagram (Ollama → agents → endpoints → dashboard), run instructions, feature table P01–P10.

- [x] **Step 5: Commit** — `feat(p10): compose stack + portfolio readme`

### Task 6: Wrap-up

**Files:**
- Create: `docs/projects/10-helpdesk-capstone/NOTES.md`

- [x] **Step 1: NOTES.md** — final retro: which MAF layers felt solid vs prerelease-rough; what you'd change building HelpDeskHQ again; where docs were wrong.
- [x] **Step 2: Commit** — `docs(p10): curriculum wrap-up`