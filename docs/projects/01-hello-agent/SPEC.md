# SPEC — P01: HelloAgent (FAQ Responder + Shared Core)

**Tier:** Basic · **Estimate:** 3–4 hours · **Depends on:** none

## Goal

Solution skeleton + shared domain core + smallest possible MAF agent. FAQ bot
answers IT questions through Ollama `glm-5.3-flash:cloud`, streamed. Everything
later projects build on.

## Concepts learned

- Solution layout, `MafDemo.Core` shared domain (used by all projects)
- `IChatClient` / `OllamaApiClient` provider wiring
- `ChatClientAgent`: name, instructions, `RunAsync` / `RunStreamingAsync`
- OpenTelemetry console exporter — first trace view

## Requirements

1. `MafDemo.sln` with `src/MafDemo.Core`, `tests/MafDemo.Core.Tests`, `src/P01.HelloAgent`.
2. `MafDemo.Core`: `Ticket` record, `TicketStatus`/`TicketPriority` enums, `ITicketStore`, `InMemoryTicketStore` — TDD, fully tested.
3. `OllamaChatClientFactory` in P01 (moves to Core later): builds `IChatClient` from `OllamaApiClient`, model name from config (`appsettings.json` + env var override).
4. Agent `FaqBot`: console one-shot run + streaming run.
5. OTel console exporter logs spans per run.
6. Instructions experiment: same prompt, two instruction sets, observable difference.

## Success criteria

- `dotnet run` (P01) prints streamed FAQ answer.
- Core tests pass: store CRUD round-trip, note append, status update.
- Console shows OTel spans for model call.
- Two instruction sets produce different answers to same prompt.

## Stretch

- Chat loop with `ReadLine` (no session yet — compare behavior with P03).

## Resources

- Get-started step 1: https://learn.microsoft.com/en-us/agent-framework/get-started/your-first-agent
- Local model quickstart: https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/chat-local-model
- .NET Blog intro: https://devblogs.microsoft.com/dotnet/introducing-microsoft-agent-framework-preview