# MAF Demo — HelpDeskHQ (C# Learning Curriculum)

[![ci](https://github.com/ihsanfarabi/maf-demo/actions/workflows/ci.yml/badge.svg)](https://github.com/ihsanfarabi/maf-demo/actions/workflows/ci.yml)

10 projects, basic to advanced, one continuous app: **HelpDeskHQ**, an IT
helpdesk assistant built on Microsoft Agent Framework (MAF) .NET.

- **Visitor tour / architecture** → [`PORTFOLIO.md`](PORTFOLIO.md)
- Per-project spec + plan + field notes: `docs/projects/NN-name/`

All 10 implemented and verified: 57 unit tests green, the capstone compose
stack serves a grounded OpenAI-compatible chat endpoint, an A2A agent endpoint,
and OTLP traces into the Aspire dashboard.

Design doc: `docs/superpowers/specs/2026-08-30-maf-csharp-curriculum-design.md`.

## Stack

- .NET 10, C#, xUnit
- MAF: `Microsoft.Agents.AI` (prerelease) — `ChatClientAgent` accepts any `IChatClient`
- Provider: Ollama localhost — `OllamaApiClient` (OllamaSharp), model `glm-5.3-flash:cloud`
- Tool calling requires `new ChatClientBuilder(client).UseFunctionInvocation().Build()`
- Observability: OpenTelemetry → Aspire dashboard (DevUI is Python-only)

## Ladder

| # | Project | MAF concepts | HelpdeskHQ story |
|---|---|---|---|
| 1 | hello-agent | ChatClientAgent, streaming, OTel | FAQ responder |
| 2 | ticket-tools | AIFunction tools, MCP, tool loop | Ticket store tools |
| 3 | session-chat | threads, sessions, persistence | Conversation survives restart |
| 4 | handbook-rag | context providers, embeddings, grounding | IT handbook answers |
| 5 | guardrail-middleware | middleware, PII redaction, tool approval, OTel | Safe operations |
| 6 | triage-composition | agents-as-tools, handoff orchestration | Routes to specialists |
| 7 | resolution-workflow | graph workflows, HITL, checkpoints | Resolution pipeline |
| 8 | harness-agent | agent harness: todos, file memory, approvals | Overnight batch agent |
| 9 | a2a-durable | A2A self-host, durable extension | Remote inventory agent |
| 10 | helpdesk-capstone | self-host, declarative YAML, evals, CI | Full product |

## Setup (once)

```bash
ollama pull glm-5.3-flash:cloud   # tool-calling + vision model, 1M ctx
ollama serve                      # ensure daemon on localhost:11434
dotnet --version                  # .NET 10
```

## Workflow per project

1. Read SPEC.md
2. Execute PLAN.md task by task (checkboxes)
3. Each task: code, verify, commit (`rtk git ...`)
4. API names come from cited doc pages in each task — docs win over plan code

## Field notes worth reading

Each project ends with `NOTES.md` recording what the MAF 1.19.0 prerelease
docs say vs what actually happens. Highlights:

- `ChatClientPromptAgentFactory` ships in `Microsoft.Agents.AI`, not
  `.Declarative` as docs imply (P10)
- Hardcoded `UseUrls()` silently overrides `ASPNETCORE_URLS` — container bound
  loopback and the published port answered nothing (P10)
- Two folders differing only in case merge on macOS and break the Docker
  build — agent YAML lives in `Definitions/` (P10)
- Durable-workflow payload typing between executors is the roughest doc-vs-
  reality gap in the prerelease (P09)
- Full index: `docs/projects/*/NOTES.md`