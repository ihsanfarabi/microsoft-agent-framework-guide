# P10 — HelpDeskCapstone notes

Capstone: one ASP.NET Core host combining the OpenAI-compatible chat endpoint,
an A2A server, declarative YAML agents, RAG grounding, an eval harness, CI,
and a Docker Compose stack.

## What worked

- **Self-hosting agents** (`Hosting` 1.19.0-preview.260822.1):
  `builder.AddAIAgent(name, factory)` + `app.MapOpenAIChatCompletions(handbookAgent,
  "/v1/chat/completions")` gives a working OpenAI-compatible endpoint in ~10
  lines. One registration-key gotcha: the key passed to `AddAIAgent` must equal
  the agent's `Name` (the factory delegate throws otherwise) — the
  `(_, _) => agent` ignore-args shape from the docs hides that the second
  parameter is the expected name.
- **Declarative agents** (`Declarative` 1.19.0-rc1): YAML agents loaded with
  `ChatClientPromptAgentFactory.CreateFromYamlAsync` behave identically to the
  equivalent C# agents from P01/P02 (spot-checked side by side).
- **A2A server** (`Hosting.A2A.AspNetCore`): keyed `AIAgent` + `AddA2AServer` +
  `MapA2AHttpJson`, agent card published at `/.well-known/agent-card.json` via
  `MapWellKnownAgentCard`.
- **Eval harness in Core**: `EvalRunner` (contains-matching) was TDD'd in
  MafDemo.Core.Tests and is reused by the live suite — evals are now plain
  library code, not project plumbing.
- **Compose**: multi-stage Dockerfile with the handbook corpus copied to
  `<approot>/docs/corpus` satisfies the walk-up `FindCorpusDirectory` (no code
  change needed for containers); `host.docker.internal:host-gateway` reaches
  host Ollama on Linux and macOS.

## Doc-vs-reality divergences

- `ChatClientPromptAgentFactory` lives in **`Microsoft.Agents.AI`**, not
  `Microsoft.Agents.AI.Declarative` as the docs imply (decompiled 1.19.0-rc1
  to confirm).
- The OpenAI hosting package is versioned **`1.19.0-alpha.260822.1`** while
  sibling packages are `1.19.0-preview.260822.1` — doc snippets assume one
  version line.
- `MapA2AHttpJson` binds HTTP+JSON (REST-style `POST {path}/message:send`), while
  `message/send` JSON-RPC is the `MapA2AJsonRpc` binding. Posting a JSON-RPC
  envelope to the HTTP+JSON path 404s with no hint.
- The prerelease packages still emit NRT warnings into consuming projects
  (CS8604 on the `AddAIAgent` name parameter — the doc pattern passes a
  possibly-null interpolated value).

## What to do differently next time

- Verify eval *expected facts* against the corpus before writing the suite:
  2 of 8 initial facts encoded remembered-not-actual handbook text (RMA window,
  won't-boot contact) and were sharpened to corpus-grounded facts.
- There is no `SkippableFact` in stock xunit: gate evals with an early-return
  `Fact` reading `RUN_EVALS` instead of pulling a skip package.
- Duplicate `appsettings.json` copies from project references fail Docker
  publish with NETSDK1152 — set `<ErrorOnDuplicatePublishOutputFiles>false>`
  in the consuming project rather than deleting the dependency's file.
- Two folders differing only in case (`Agents/` with C# sources, `agents/`
  with YAML) merge into one on macOS's case-insensitive filesystem and the
  Docker build loses one. Agent definitions live in `Definitions/`.
- Hardcoded `builder.WebHost.UseUrls(...)` overrides `ASPNETCORE_URLS`, so the
  container silently bound to `localhost:5080` *inside* the container and the
  published port answered nothing. Ports belong in launchSettings (local) and
  `ASPNETCORE_URLS` (compose), never in code.
- The `aspire-dashboard` standalone image (13.5.2 line) rejects
  `DASHBOARD__FRONTEND__AUTHMODE=Unauthenticated` with a FormatException even
  when `DOTNET_DASHBOARD_UNSECURE_ALLOW_ANONYMOUS=true` is set — keep the
  default BrowserToken and take the login token from the container log
  (`login?t=…`). The dashboard's data plane is gRPC-web, so traces can't be
  read with curl; visibility was verified by token login + zero OTLP export
  errors in the app log.
- CI eval gating: standard runners cannot pull the two Ollama models, so evals
  run only on `workflow_dispatch` (manual) — hermetic unit tests stay on push.

## Curriculum wrap-up

Solidest MAF layers (stable, doc-matching): ChatClient abstractions, tool
calling, structured output, workflows in-process. Prerelease-rough: durable
workflow payload typing (see P09 notes), declarative namespace, OpenAI hosting
naming/versions. Biggest gap between docs and reality across the series was
payload serialization between durable executors — worth re-checking each
preview bump.