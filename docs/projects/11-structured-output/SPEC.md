# SPEC — P11: StructuredOutput

Typed JSON responses from a MAF agent on Ollama — honestly framed around the
Ollama Cloud schema limitation.

## Story

HelpDeskHQ's triage classifier (P07) prompt-asks for one word and defends with
a fallback. Upgrade: model returns a **typed** `TriageDecision` (category,
priority, summary) and a **typed** `TicketDraft` extraction.

## Success criteria

- `agent.RunAsync<TriageDecision>(...)` returns a typed `AgentResponse<TriageDecision>` — no manual JSON parse in the happy demo path.
- All three format paths exercised: typed `RunAsync<T>`; per-call `ChatClientAgentRunOptions` with `ChatResponseFormat.ForJsonSchema<T>()`; a raw `JsonElement` schema whose text is deserialized manually.
- Demo surfaces the cloud reality: on `glm-5.3-flash:cloud` the schema is silently ignored (Ollama Cloud does not implement structured outputs, ollama/ollama#12362) — the code asserts schema compliance, shows the failure, and falls back to a prompt-embedded schema + tolerant parse.
- Known-good path documented: set `OLLAMA_MODEL` to a local model and the same code enforces.

## Non-goals

No changes to existing P01-P10 code. Streaming not required.

## Verified API surface (MAF 1.19.0, installed-package cross-check)

- Typed run: `AIAgent.RunAsync<T>(string message, AgentSession?, JsonSerializerOptions?, ChatClientAgentRunOptions?, CancellationToken)` → `AgentResponse<T>`, result at `.Result`.
- Runtime format: `ChatClientAgentRunOptions` (inherits `AgentRunOptions`) with `.ResponseFormat = ChatResponseFormat.ForJsonSchema<T>()` (M.E.AI.Abstractions 10.9.0).
- Raw schema: `ChatResponseFormat.ForJsonSchema(JsonElement.Parse(schema), "Name", "Description")`; read `response.Text`, deserialize manually.
- Init-time format: `ChatClientAgentOptions.ChatOptions.ResponseFormat`.
- Streaming equivalent: `RunStreamingAsync` → `ToAgentResponseAsync()` → `.Text`.
- Constraints: `ChatClientAgent` only; no primitives/arrays as `T` (wrapper record required); fallback decorator LLM-converts when provider lacks support.

## Resources

- Doc: https://learn.microsoft.com/en-us/agent-framework/agents/structured-outputs
- Sample: https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput
- Ollama: https://docs.ollama.com/capabilities/structured-outputs · https://github.com/ollama/ollama/issues/12362