# P11 — StructuredOutput notes

Structured output: typed `RunAsync<T>`, the three response-format paths
(agent-level, per-call options, raw run + manual deserialize), and a
schema-compliance probe with a one-retry fallback decorator — all against
Ollama `glm-5.3-flash:cloud`.

## What worked

- **Typed runs** (`Microsoft.Agents.AI` 1.19.0):
  `AIAgent.RunAsync<T>(message, session, serializerOptions, options, ct)` →
  `Task<AgentResponse<T>>` compiles and returns a real `TriageDecision` live.
  MAF generates and attaches the JSON schema itself (`ForJsonSchema<T>` with
  draft-2020-12, PascalCase properties named after the type).
- **Schema-name probe**: an `IChatClient` recorder (`ResponseFormatRecorder`)
  placed between Ollama and the agent printed the ResponseFormat that actually
  reached the model for every path. This turned "which format wins" from a
  docs question into an observed fact (see the `RunAsync<T>` override below).
- **Fence-coercion middleware**: a small `JsonFenceCoercionClient` wrapper
  around the caller's `IChatClient` rewrites ```` ```json ````-fenced model
  text to bare JSON before MAF deserializes — `AgentResponse<T>.Result` does
  *not* tolerate fences
  (`` `'`' is an invalid start of a value` ``).
- **Fallback decorator**: `ComplianceFallback.RunJsonWithFallbackAsync<T>`
  runs, probes the raw text as `T`, and re-prompts exactly once with the
  probe's failure reason plus the schema embedded in the prompt. Hermetic
  coverage via a scripted fake `IChatClient` (out-of-range enum on call 1,
  fenced valid JSON on call 2) — the re-prompt path is proven without
  depending on a nondeterministic cloud model misbehaving.
- **Enum drift fixed in instructions**: with the schema unenforced (below),
  the model occasionally invented category values (`"Boot Failure"`). After
  the agent instructions enumerated the exact allowed `Category`/`Priority`
  values, the live typed test passed 3/3 consecutive runs.

## Doc-vs-reality divergences

- **`ChatClientAgentRunOptions.ResponseFormat` doesn't exist** in 1.19.0
  (members: `ChatOptions`, `ChatClientFactory`). The doc-shaped
  `new ChatClientAgentRunOptions { ResponseFormat = ... }` predates this
  API; format lives under `.ChatOptions`:
  `new ChatClientAgentRunOptions { ChatOptions = new ChatOptions { ResponseFormat = ... } }`.
- **Generic `RunAsync<T>` always injects its own `ForJsonSchema<T>`** and
  silently discards any per-call `ResponseFormat` — source-verified in
  `AIAgentStructuredOutput.cs` (tag `dotnet-1.19.0`): it clones the options
  and overwrites `ResponseFormat` unconditionally. Proven at runtime with a
  distinctive `schemaName: "PerCallTriageDecision"`; the model received
  `name=TriageDecision` (MAF's own) regardless. A user-chosen schema with
  typed deserialization therefore has to go through the **non-generic**
  `RunAsync` + manual deserialize, where the caller's per-call format does
  survive (model received `name=TicketDraft` on that path).
- **`JsonSchema.For<T>()` doesn't exist** in MEAI 10.9 — the escape hatch is
  `AIJsonUtilities.CreateJsonSchema(typeof(T))` (the same generator
  `ForJsonSchema<T>` uses internally). Also gone: `ChatResponseFormatJsonSchema`
  — schema formats are `ChatResponseFormatJson` with `Schema`/`SchemaName`/
  `SchemaDescription`.
- **Ollama Cloud (`:cloud`) silently ignores schemas** (ollama/ollama#12362):
  direct `curl` to `/api/chat` with `format` showed schema keys ignored — the
  schema reached the model as advisory only. Correctness rests on
  instructions + fence coercion + the probe/fallback, not the schema. A
  local (non-cloud) model would enforce it and make the fallback a no-op.
- **`AgentResponse<T>.Result` deserialization is case-sensitive** against
  camelCase Ollama output: default serializer options + the model emitting
  `category`/`priority`/`summary` → null/garbled fields. Fix: pass
  case-insensitive `JsonSerializerOptions` as `serializerOptions:` to
  `RunAsync<T>` (and reuse the same options in the probe so both paths
  validate identically).
- The brief's live test asserted `TicketCategory.Undefined`, which doesn't
  exist on the enum — tests assert `Enum.IsDefined` + non-empty `Summary`
  instead.

## What to do differently next time

- Before writing any per-call format code, decide which run form it needs:
  generic `RunAsync<T>` = your format is overwritten; non-generic `RunAsync`
  = your format survives. The schema-name probe (distinctive `schemaName`)
  is a cheap way to prove the routing on any new model/provider.
- Treat cloud-model structured output as advisory from the start: write the
  instructions with enumerated allowed values on day one, and put a
  validating probe in the library (not in the demo) so the fallback has
  something honest to measure.
- Centralize one `JsonSerializerOptions` (case-insensitive, enum-as-string,
  fence-tolerant normalization) and use it for typed runs, probes, and the
  manual raw-path deserialize — diverging options caused the first
  case-sensitivity failure.
- The fence-coercion middleware covers non-streaming only; a streaming typed
  run needs a streaming equivalent.
- Brief-literal snippets decay fast on the prerelease line: two of this
  project's three format APIs (`ChatClientAgentRunOptions.ResponseFormat`,
  `JsonSchema.For<T>()`) don't exist in the installed packages. Verify the
  type surface in the NuGet XML docs / decompiled source before copying.
