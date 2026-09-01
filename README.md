# MAF Demo — HelpDeskHQ (C# Learning Curriculum)

[![ci](https://github.com/ihsanfarabi/maf-demo/actions/workflows/ci.yml/badge.svg)](https://github.com/ihsanfarabi/maf-demo/actions/workflows/ci.yml)

15 projects, basic to advanced, one continuous app: **HelpDeskHQ**, an IT
helpdesk assistant built on Microsoft Agent Framework (MAF) .NET.

- **Visitor tour / architecture** → [`PORTFOLIO.md`](PORTFOLIO.md)
- Per-project spec + plan + field notes: `docs/projects/NN-name/`

All 15 implemented and verified: 111 unit tests green, the capstone compose
stack serves a grounded OpenAI-compatible chat endpoint, an A2A agent endpoint,
and OTLP traces into the Aspire dashboard, and one workflow graph spans two
remote A2A agent services with a conditional edge skipping a dead hop.

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
| 11 | structured-output | typed `RunAsync<T>`, JSON response formats | Typed ticket triage |
| 12 | mcp-knowledge-server | custom MCP stdio server, MCP client | Handbook knowledge server for the ticket bot |
| 13 | streaming-approval | `UseToolApproval`, SSE streaming, HITL | Delete needs an operator vote mid-stream |
| 14 | semantic-memory | `ChatHistoryMemoryProvider`, custom `AIContextProvider`, MEVD vector store | Remembers your preferences across process restarts |
| 15 | distributed-workflow | graph workflows across A2A, conditional edges, failure visibility | One graph, two remote agent hops — skip a dead one, visibly |

## Setup (once)

```bash
ollama pull glm-5.3-flash:cloud   # tool-calling + vision model, 1M ctx
ollama pull bge-m3                # embeddings (RAG + semantic memory)
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
- `MapOpenAIChatCompletions` silently drops approval content (`_ => null` in
  the content switch, no inbound response channel) — why P13's approval flow
  needs a custom SSE endpoint (P13)
- The real 1.19.0 `ChatHistoryMemoryProvider` takes a single ctor (no fluent
  `.State(...)`), scopes are nullable-string values with no `Search` field,
  and `InMemoryVectorStore` is process-local — durable memory needs the
  custom provider + file-backed fact store (P14)
- Agent nodes are `ChatProtocol` executors: an agent node's edge payload is
  `List<ChatMessage>` + `TurnToken`, never `AgentResponse` — a condition typed
  on the plan's snippet can never fire, silently (P15)
- Generic `RunAsync<T>` always injects its own `ForJsonSchema<T>` and silently
  discards a per-call ResponseFormat — a caller-chosen schema needs the
  non-generic run + manual deserialize (P11)
- The MCP C# SDK snake_cases tool names on the wire: the C# method surfaces
  as `search_knowledge`, matching the SDK's default, not the method name (P12)
- Full index: `docs/projects/*/NOTES.md`
