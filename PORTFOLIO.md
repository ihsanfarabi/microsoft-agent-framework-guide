[![ci](https://github.com/ihsanfarabi/maf-demo/actions/workflows/ci.yml/badge.svg)](https://github.com/ihsanfarabi/maf-demo/actions/workflows/ci.yml)

# MafDemo — Microsoft Agent Framework curriculum

Ten projects that walk the Microsoft Agent Framework (MAF) from a one-file
chatbot to a durable multi-agent workflow — all on a local Ollama model, all
runnable end to end. Built against the 1.19.0 prerelease line; API
divergences from the docs are recorded per project in `docs/projects/*/NOTES.md`.

## Architecture

```mermaid
flowchart LR
    subgraph P10 - HelpDeskHQ capstone
        Chat[OpenAI-compatible<br/>/v1/chat/completions] --> H[HandbookBot<br/>RAG-grounded]
        A2A[A2A HTTP+JSON<br/>/a2a/faq] --> F[FaqBot<br/>declarative YAML]
        H --> R[HandbookRetriever<br/>bge-m3 vectors]
        F --> R
        H --> O[Ollama<br/>glm-5.3-flash]
        F --> O
    end
    subgraph P09 - Durable host
        D[ResolutionWorkflow<br/>diagnose - approve - fix] --> DT[Durable Task Scheduler<br/>dts-emulator]
    end
    subgraph P06/P07
        W[Workflow graph<br/>executors + edges] --> P07[P07 resolution]
    end
```

## Projects

| # | Project | MAF feature | Highlights |
|---|---------|-------------|------------|
| 01 | HelloAgent | `AIAgent` + ChatClient | FAQ bot, OTLP telemetry, one-shot + REPL |
| 02 | FunctionTools | `[Description]` tool calling | Agent calls local C# functions |
| 03 | StructuredOutput | output schemas | Typed JSON responses |
| 04 | HandbookRag | RAG grounding | Chunk + embed handbook, context-provider injection |
| 05 | MultiAgent | agent-as-tool / handoffs | Multiple specialist agents composed |
| 06 | RemoteAgentsA2A | A2A client | Consume a remote agent as a tool |
| 07 | ResolutionWorkflow | Workflows | Executors, edges, conditional routing, shared state |
| 08 | AgentSessions | Session persistence | Resume conversations across restarts, checkpoints |
| 09 | DurableHost | Durable workflows + A2A server | Kill-and-resume, DTS emulator, hosted A2A endpoint |
| 10 | HelpDeskCapstone | Everything together | OpenAI-compatible chat + A2A server + declarative YAML agents + RAG + evals + CI + compose |

## Run

Prereqs: .NET 10 SDK, [Ollama](https://ollama.com) with the models from
`appsettings.json` (`glm-5.3-flash:cloud`, `bge-m3`).

Capstone (one command, includes the Aspire dashboard at http://localhost:18888):

```bash
ollama serve   # if not already running
docker compose up --build
curl -s http://localhost:5080/v1/chat/completions -H 'Content-Type: application/json' \
  -d '{"model":"HandbookBot","messages":[{"role":"user","content":"How do I reset my password?"}]}'
```

Any single project:

```bash
dotnet run --project src/P01.HelloAgent            # every project: P01..P10
RUN_EVALS=1 dotnet test tests/MafDemo.Core.Tests --filter EvalSuite
```

Durable resume (P09): start the dts-emulator container, run the host, interrupt
it mid-approval, then `dotnet run -- resume` — pending work re-runs from
scheduler state. Full walkthrough in `docs/projects/09-a2a-durable/NOTES.md`.

## Layout

- `src/P*` — one folder per curriculum project; `MafDemo.Core` holds shared
  handbook corpus loading, chunking, and the eval harness.
- `docs/corpus` — the MafCorp handbook markdown the RAG projects index.
- `docs/projects/<n>-*/PLAN.md` — per-project learning plan, `NOTES.md` —
  findings and doc-vs-reality divergences.
- `agents/` (inside P10) — declarative YAML agent definitions.
- `.github/workflows/ci.yml` — build + unit tests on push; eval suite gated to
  manual dispatch (needs live Ollama).