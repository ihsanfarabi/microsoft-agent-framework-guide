# MAF C# Learning Curriculum — "HelpDeskHQ" — Design

**Date:** 2026-08-30
**Status:** Approved design (pending user spec review)
**Path:** Architectural — 10 sub-projects, each gets its own SPEC.md + PLAN.md

## Purpose

Learn Microsoft Agent Framework (MAF) basic to advanced, in C#, by building one
continuous application: **HelpDeskHQ**, an IT helpdesk assistant. Ten projects,
each adding one MAF capability tier. Final result is a portfolio-worthy,
self-hosted, observable, multi-agent helpdesk app.

## Decisions (from brainstorming)

| Decision | Choice | Rationale |
|---|---|---|
| Language | C# / .NET 10 | User is strong .NET dev; MAF .NET is flagship |
| Model | Ollama `glm-5.3-flash:cloud` via localhost:11434 | Tool-calling + vision + thinking, 1M ctx, MIT. Cloud inference through local Ollama daemon; no Azure keys, single config point |
| Cloud | None (no Azure) | All hosting self-hosted. P10 capstone = self-hosted production |
| Repo | One solution `MafDemo.sln`, subfolder per project | Cross-project reference, one checkout |
| Theme | One continuous domain (IT helpdesk) | Every MAF concept maps to a real helpdesk need |
| Debugger | OpenTelemetry + Aspire dashboard (standalone) | DevUI is Python-only today (C# docs "coming soon"); Aspire dashboard fills trace-viewing gap |
| Testing | xUnit; TDD for pure domain logic in `MafDemo.Core`; agent runs verified by console harnesses + trace inspection | Agent loops aren't sensibly unit-testable; domain logic is |

## Stack

- .NET 10, C#, xUnit
- MAF packages: `Microsoft.Agents.AI` (prerelease feed) + `OllamaSharp` for the
  provider. Verified pattern: `OllamaApiClient` implements `IChatClient`;
  `ChatClientAgent` accepts any `IChatClient`. **Tool calling requires
  `new ChatClientBuilder(ollamaClient).UseFunctionInvocation().Build()`** — raw
  Ollama client won't run the tool loop. Agents registered via DI:
  `builder.AddAIAgent(name, (sp, key) => new ChatClientAgent(...))`.
  Later projects: `Microsoft.Agents.AI.Declarative` (YAML agents,
  `CreateFromYamlAsync`), `Microsoft.Agents.AI.Workflows.Declarative`
  (trigger-based workflow YAML), Durable Extension base package (bring-your-own
  compute, `ConfigureDurableOptions` in a plain host). Exact names still cited
  per plan task from official docs — remaining drift risk is orchestration and
  harness class names, not the Ollama path.
- Recent Ollama (cloud-model support) with `glm-5.3-flash:cloud` pulled
- OpenTelemetry + Aspire dashboard (standalone mode)
- docker-compose (P10): app + Aspire dashboard

## Repo layout

```
maf-demo/
  MafDemo.sln
  src/
    MafDemo.Core/           # shared domain: Ticket, TicketStore (in-memory + JSON file),
                            # handbook corpus docs, ticket history sample data
    P01.HelloAgent/         # console app per project
    P02.TicketTools/
    P03.SessionChat/
    P04.HandbookRag/
    P05.GuardrailMiddleware/
    P06.TriageComposition/
    P07.ResolutionWorkflow/
    P08.HarnessAgent/
    P09.A2aDurable/
    P10.HelpDeskCapstone/   # ASP.NET Core self-hosted app
  tests/
    MafDemo.Core.Tests/
    P0N.*.Tests/            # per-project test projects (where testable logic exists)
  docs/
    superpowers/specs/      # this design doc
    projects/0N-name/SPEC.md + PLAN.md   # per-project spec + implementation plan
```

## Ladder

| # | Project | MAF concepts | HelpdeskHQ story |
|---|---|---|---|
| 1 | P01.HelloAgent | AIAgent / ChatClientAgent, IChatClient, streaming | FAQ responder answers IT questions |
| 2 | P02.TicketTools | AIFunction tools, tool loop, MCP server | Create/list/update tickets; tools = ticket store + knowledge lookup |
| 3 | P03.SessionChat | AgentThread / sessions, thread persistence, context | Conversation with user context; session survives restart |
| 4 | P04.HandbookRag | Context providers, embeddings, chunking, grounding | Answers grounded in company IT handbook |
| 5 | P05.GuardrailMiddleware | Middleware, PII redaction, tool approval, OpenTelemetry | Redacts employee IDs; approval before destructive ticket ops |
| 6 | P06.TriageComposition | Agents-as-tools, handoff orchestration | Triage routes to Network / Software / Hardware specialists |
| 7 | P07.ResolutionWorkflow | Graph workflows, orchestrations, HITL, checkpoints | Resolution pipeline: triage → diagnose → approve fix → resolve, resumable |
| 8 | P08.HarnessAgent | Agent harness: todos, file memory, approvals, compaction | Long-haul overnight ticket-batch agent |
| 9 | P09.A2aDurable | A2A protocol self-host, durable extension | Inventory agent on separate A2A service; workflow survives host restart |
| 10 | P10.HelpDeskCapstone | ASP.NET Core host, declarative YAML agent, evals, CI | Full self-hosted product: web API + compose + eval suite |

## Shared core contract (`MafDemo.Core`)

- `Ticket` record: `Id, Title, Description, Priority, Status, Assignee, CreatedAt, Notes`
- `ITicketStore` interface: `Create, Get, List, Update, AddNote`
- `InMemoryTicketStore` (default) + `FileTicketStore` (JSON, used P3+)
- `Handbook` corpus: 10+ markdown docs (`docs/corpus/*.md`) — VPN policy, password
  reset, hardware RMA, software install policy, Wi-Fi, email, security incident,
  onboarding, license, backup policy
- All pure logic xUnit-tested up front (P1 task), never re-implemented per project

## Verification strategy per project

1. xUnit tests for domain/tool/provider logic
2. Console harness run: scripted scenario with expected observable behavior
3. Trace check: OTel spans in Aspire dashboard (P5+ mandatory; P1-P4 console exporter)
4. Manual scenario checklist in each PLAN

## Risks / open items

- **R1 — API drift (narrowed):** Ollama provider path, DI registration,
  declarative packages, and durable self-host verified against current docs
  (2026-08-30). Remaining risk: orchestration builder names (P6/P7) and harness
  extension surface (P8). Every plan task cites its doc page; copy names from
  docs, never memory.
- **R2 — Model quality:** glm-5.3-flash:cloud is cloud-metered by Ollama (free
  tier exists); if tool-calling quality degrades mid-curriculum, fallback chain:
  `glm-5.3-flash:cloud` → any Ollama `tools`-tagged model.
- **R3 — Durable extension self-host:** P9/P10 self-hosted durable workflows are
  the least-documented corner; plan includes doc-research task as explicit step.
- **R4 — DevUI C#:** not available; if it ships mid-curriculum, swap Aspire
  trace-check steps for DevUI where convenient.

## Deliverables

1. This design doc (committed)
2. Per project: `docs/projects/0N-name/SPEC.md` + `PLAN.md`
3. Code, built project by project after each spec+plan pair is approved

## Stale artifacts

Python-based SPEC/PLAN drafts from earlier session (docs/projects/01–04) will be
replaced with C# versions; Python files deleted before new specs land.