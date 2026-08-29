# SPEC — P10: HelpDeskCapstone (Self-Hosted Product)

**Tier:** Advanced · **Estimate:** 8–12 hours · **Depends on:** P01–P09

## Goal

Portfolio piece: HelpDeskHQ as one self-hosted ASP.NET Core app — protocol
endpoints, two agents defined declaratively in YAML, an eval suite, CI, and
one-command Docker Compose startup. Proves you can take MAF from lab to product.

## Concepts learned

- Self-hosting with `Microsoft.Agents.AI.Hosting` + protocol packages (OpenAI-compatible endpoint, A2A endpoint)
- Declarative YAML agents: `Microsoft.Agents.AI.Declarative`, `CreateFromYamlAsync`
- Eval harness: table-driven (input → must-contain-fact) assertions against the RAG agent
- CI for agents: build + test + evals on every push
- Compose + OTLP wiring to Aspire dashboard

## Requirements

1. `src/P10.HelpDeskCapstone` — ASP.NET Core app hosting, via DI:
   - `HandbookRagAgent` (P04) at an OpenAI-compatible chat endpoint
   - `FaqBot` + triage router redefined in YAML (`agents/faq.yaml`, `agents/triage.yaml`), loaded via `CreateFromYamlAsync` at startup — no agent C# for these two
   - A2A endpoint exposing the resolution workflow (P07) or FaqBot
2. `evals/` — xUnit eval harness: ≥8 cases of (input, expected fact); asserts grounded answer contains fact (case-insensitive); prints pass rate; gated by env `RUN_EVALS=1` so CI runs it.
3. `.github/workflows/ci.yml` — on push: `dotnet build`, `dotnet test`, eval run.
4. `docker-compose.yml` — app + Aspire dashboard; OTLP env wired; Ollama assumed on host (`host.docker.internal:11434`).
5. Root `PORTFOLIO.md` — mermaid architecture diagram, run instructions, feature list per project P01–P10.
6. Success test: fresh clone → `docker compose up` → `curl` chat endpoint → grounded handbook answer.

## Success criteria

- Chat endpoint answers handbook questions grounded in corpus.
- YAML agents load at startup and respond identically to their P01/P06 C# predecessors (spot-check).
- Eval suite ≥8/8 green in CI.
- `PORTFOLIO.md` renders mermaid diagram; compose one-command startup works.

## Stretch

- Minimal web UI chat page (AG-UI protocol or plain SSE).
- Declarative workflow YAML (`Microsoft.Agents.AI.Workflows.Declarative`) replacing P07 graph code.

## Resources

- Self-hosting (verified): https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting
- A2A hosting .NET (verified): https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/a2a/dotnet
- Declarative agents (verified): https://learn.microsoft.com/en-us/agent-framework/agents/declarative
- Declarative workflows: https://learn.microsoft.com/en-us/agent-framework/workflows/declarative
- Evals/tracing samples: https://github.com/microsoft/Agent-Framework-Samples/blob/main/08.EvaluationAndTracing/README.md