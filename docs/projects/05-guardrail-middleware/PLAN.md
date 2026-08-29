# P05: GuardrailMiddleware — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ticket agent wrapped in logging + PII-redaction run middleware and a tool-approval function middleware; traces exported to Aspire dashboard.

**Architecture:** `PiiRedactor` pure class in `MafDemo.Core` (TDD). Middlewares are static functions in P05 composed via `agent.AsBuilder().Use(...).Use(...).Build()` (verified API from middleware doc). Telemetry class switches OTLP exporter to Aspire.

**Tech Stack:** .NET 10, `Microsoft.Agents.AI` (prerelease), `OllamaSharp`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, xUnit, Aspire dashboard container.

**Spec:** `docs/projects/05-guardrail-middleware/SPEC.md`

## Global Constraints

- Model `glm-5.3-flash:cloud` via `OllamaChat.Create()` (P01 pattern); tools require `new ChatClientBuilder(client).UseFunctionInvocation().Build()`.
- Verified signatures (middleware doc, 2026-08): run middleware `Task<AgentResponse> Func(IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent innerAgent, CancellationToken)`; function middleware `ValueTask<object?> Func(AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken)`. If compile errors, copy from https://learn.microsoft.com/en-us/agent-framework/concepts/agents/middleware.
- Function calling middleware only works with `FunctionInvokingChatClient` (wired by `UseFunctionInvocation`).
- Commit after every task: `rtk git add -A && rtk git commit -m "..."`.

---

### Task 1: PiiRedactor in Core (TDD)

**Files:**
- Create: `src/MafDemo.Core/Guardrails/PiiRedactor.cs`
- Test: `tests/MafDemo.Core.Tests/PiiRedactorTests.cs`

**Interfaces:**
- Produces: `static class PiiRedactor { string Redact(string text); }` — used by P05 middleware, later projects

- [ ] **Step 1: Write failing tests**

```csharp
using MafDemo.Core.Guardrails;

public class PiiRedactorTests
{
    [Theory]
    [InlineData("Employee EMP-44555 says hi", "Employee [REDACTED-ID] says hi")]
    [InlineData("EMP-123456 and EMP-1234", "[REDACTED-ID] and [REDACTED-ID]")]
    [InlineData("mail me at jane.doe@contoso.com please", "mail me at [REDACTED-EMAIL] please")]
    [InlineData("EMP-12 is too short", "EMP-12 is too short")]
    [InlineData("clean text", "clean text")]
    public void Redact_patterns_and_leave_clean_text(string input, string expected)
        => Assert.Equal(expected, PiiRedactor.Redact(input));
}
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test`

- [ ] **Step 3: Implement**

```csharp
using System.Text.RegularExpressions;

namespace MafDemo.Core.Guardrails;

public static partial class PiiRedactor
{
    [GeneratedRegex(@"EMP-\d{4,6}")]
    private static partial Regex EmployeeId();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+")]
    private static partial Regex Email();

    public static string Redact(string text) =>
        Email().Replace(EmployeeId().Replace(text, "[REDACTED-ID]"), "[REDACTED-EMAIL]");
}
```

- [ ] **Step 4: Run, verify PASS** — `dotnet test`

- [ ] **Step 5: Commit** — `feat(core): pii redactor with tests`

### Task 2: Logging + redaction run middleware

**Files:**
- Create: `src/P05.GuardrailMiddleware/P05.GuardrailMiddleware.csproj`, `Program.cs`, `TicketAgent.cs`, `Middleware/RunMiddlewares.cs`
- Create: `tests/P05.GuardrailMiddleware.Tests/P05.GuardrailMiddleware.Tests.csproj` (reference Core tests reuse `PiiRedactor` — no new tests here yet)

**Interfaces:**
- Consumes: `PiiRedactor.Redact`, `ITicketStore`, `OllamaChat.Create`
- Produces: `static class RunMiddlewares { Logging(); Redaction(); }` returning middleware delegates; `TicketAgent.Create(ITicketStore store)`

- [ ] **Step 1: Scaffold**

```bash
dotnet new console -n P05.GuardrailMiddleware -o src/P05.GuardrailMiddleware -f net10.0
dotnet sln add src/P05.GuardrailMiddleware
dotnet add src/P05.GuardrailMiddleware reference src/MafDemo.Core
dotnet add src/P05.GuardrailMiddleware package Microsoft.Agents.AI --prerelease
dotnet add src/P05.GuardrailMiddleware package OllamaSharp
```

- [ ] **Step 2: Ticket agent with tools** — port tool functions from P02 (`create_ticket`, `get_ticket`, `update_ticket_status` over `ITicketStore`, via `AIFunctionFactory.Create`), chat client wrapped with `UseFunctionInvocation()`.

- [ ] **Step 3: Write run middlewares** — signatures from Global Constraints; transform input by redacting last user message, redact output text after inner run:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MafDemo.Core.Guardrails;

public static class RunMiddlewares
{
    public static Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>> Logging() =>
        async (messages, session, options, innerAgent, ct) =>
        {
            Console.WriteLine($"[log] run start: {messages.Count()} message(s)");
            var response = await innerAgent.RunAsync(messages, session, options, ct);
            Console.WriteLine($"[log] run end: {response.Messages.Count} message(s)");
            return response;
        };

    public static Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>> Redaction() =>
        async (messages, session, options, innerAgent, ct) =>
        {
            var redacted = messages.Select(m => m.Role == ChatRole.User && !string.IsNullOrEmpty(m.Text)
                ? new ChatMessage(m.Role, PiiRedactor.Redact(m.Text)) : m);
            var response = await innerAgent.RunAsync(redacted, session, options, ct);
            // redact assistant output text before returning (member names: verify against doc)
            return response;
        };
}
```
(Streaming variant: same logic with `RunStreamingAsync` + `yield return`; required pair per doc — when only non-streaming func provided, it is used for both.)

- [ ] **Step 4: Compose and run**

```csharp
var baseAgent = TicketAgent.Create(store);
var agent = baseAgent
    .AsBuilder()
    .Use(runFunc: RunMiddlewares.Logging(), runStreamingFunc: null)
    .Use(runFunc: RunMiddlewares.Redaction(), runStreamingFunc: null)
    .Build();
```
Run a benign ask; expect `[log]` lines and normal answer.

- [ ] **Step 5: Commit** — `feat(p05): logging + pii redaction run middleware`

### Task 3: Tool approval function middleware

**Files:**
- Create: `src/P05.GuardrailMiddleware/Middleware/ToolApprovalMiddleware.cs`
- Modify: `src/P05.GuardrailMiddleware/Program.cs` (add `.Use(ToolApprovalMiddleware.Create())` to builder chain)

**Interfaces:**
- Produces: `static class ToolApprovalMiddleware { Create(Func<string, bool>? prompt = null); }` — injectable prompt delegate for testing

- [ ] **Step 1: Write middleware** — verified function-calling signature; gate `update_ticket_status` when status arg is `Closed`:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public static class ToolApprovalMiddleware
{
    public static Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> Create(Func<string, bool>? prompt = null) =>
        async (agent, context, next, ct) =>
        {
            var isDestructive = context.Function.Name == "update_ticket_status"
                && context.Arguments?.GetValue("status")?.ToString() is "Closed";
            if (!isDestructive)
                return await next(context, ct);

            Console.WriteLine($"[approval] {context.Function.Name} args: {context.Arguments}");
            Console.Write("approve? (y/n): ");
            var ok = prompt?.Invoke("") ?? (Console.ReadLine()?.Trim().ToLowerInvariant() == "y");
            return ok ? await next(context, ct)
                      : "Rejected by operator approval. Do not close the ticket.";
        };
}
```
(Member names like `context.Arguments` — verify against middleware doc sample.)

- [ ] **Step 2: Unit test with injected prompt** — new test in `tests/MafDemo.Core.Tests` or a P05 test project: `Create(prompt: _ => false)` returns rejection string without calling `next` (assert via captured flag in a stub `next`).

- [ ] **Step 3: Run scenario** — seed ticket, prompt: `"Employee EMP-44555 says the VPN issue on ticket <id> is fixed — close it"`. Expect redaction log, approval prompt, `y` → ticket Closed in store.

- [ ] **Step 4: Run rejection path** — same, answer `n`. Expect status unchanged, model asks/apologizes.

- [ ] **Step 5: Commit** — `feat(p05): tool approval middleware`

### Task 4: OTel to Aspire dashboard

**Files:**
- Create: `src/P05.GuardrailMiddleware/Telemetry.cs`
- Create: `aspire-dashboard.sh` (docker run one-liner)

- [ ] **Step 1: Start Aspire**

```bash
docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

- [ ] **Step 2: Switch exporter** — `OpenTelemetry.Exporter.OpenTelemetryProtocol` package; `OtlpExporter` endpoint `http://localhost:4317` (env `OTEL_EXPORTER_OTLP_ENDPOINT`); keep the MAF source name from P01 Telemetry.cs.

- [ ] **Step 3: Run scenario, open http://localhost:18888 → Traces.** Expect: agent run span, model call span, function call span. Confirm `EMP-44555` absent from model-input span contents.

- [ ] **Step 4: Commit** — `feat(p05): otlp export to aspire dashboard`

### Task 5: NOTES

**Files:**
- Create: `docs/projects/05-guardrail-middleware/NOTES.md`

- [ ] **Step 1:** Record: where each middleware type sits in the pipeline; redaction in run vs IChatClient middleware (stretch if done); what the trace showed.
- [ ] **Step 2: Commit** — `docs(p05): learning notes`