# SPEC — P05: GuardrailMiddleware (Logging, PII Redaction, Tool Approval, Aspire)

**Tier:** Intermediate · **Estimate:** 4–5 hours · **Depends on:** P04

## Goal

Take the P02-style ticket agent and make it safe and observable: three middlewares (logging, PII redaction, tool approval) plus OTel export into the Aspire dashboard. First project where observability is mandatory.

## Concepts learned

- Three .NET middleware types: agent run middleware, function calling middleware, IChatClient middleware
- `agent.AsBuilder().Use(runFunc, runStreamingFunc).Use(functionMiddleware).Build()` chain
- Input/output transformation in run middleware (redact before model, redact after)
- Gating tool calls in function calling middleware (approval, rejection)
- OTel OTLP export to Aspire dashboard standalone

## Requirements

1. `P05.GuardrailMiddleware` console app with a ticket agent (tools: `create_ticket`, `get_ticket`, `update_ticket_status` from P02) wired through `ChatClientBuilder(...).UseFunctionInvocation()`.
2. **Logging run middleware**: logs input message count, output message count, every run. Non-blocking.
3. **PII redaction run middleware**: regex-redacts employee IDs (`EMP-\d{4,6}`) and emails from input before the model sees them, and from output after. `PiiRedactor` is a pure class in `MafDemo.Core` (TDD).
4. **Tool approval function middleware**: any `update_ticket_status` call targeting `Closed` (or a new `delete_ticket` tool) prints the call + args, prompts `y/n` in console; `n` blocks the call and returns a rejection string to the model.
5. OTel export switched from console (P01) to Aspire dashboard standalone (docker), OTLP endpoint via env var.
6. Story scenario end-to-end: "Employee EMP-44555 says the VPN issue on ticket <id> is fixed — close it" → redacted input logged, approval prompt, `y` → ticket Closed.

## Success criteria

- Scenario above completes with ticket Closed and `EMP-44555` never appearing in the raw model input (visible in Aspire trace).
- Answering `n` at approval leaves ticket status unchanged and model apologizes/asks.
- `PiiRedactor` tests pass: EMP ids, emails, mixed text, no false positives on `EMP-12` (too short).
- Aspire dashboard shows spans: model call, function call, agent run.

## Stretch

- IChatClient middleware variant of redaction (compare intercept points vs run middleware — redaction inside the tool loop vs outside).
- Rate-limit middleware: max 5 runs per minute.

## Resources

- Middleware (C#): https://learn.microsoft.com/en-us/agent-framework/concepts/agents/middleware
- Observability: https://learn.microsoft.com/en-us/agent-framework/agents/observability
- Aspire dashboard standalone: https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone