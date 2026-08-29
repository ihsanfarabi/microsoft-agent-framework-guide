# P04: HandbookRag — Notes

## Task 4: Guardrail verification

**Auto-injection vs tool retrieval.** With `HandbookContextProvider` (an `AIContextProvider` registered in `ChatClientAgentOptions.AIContextProviders`), retrieval is not a decision the model makes: on every `RunAsync` the provider embeds the latest user message, runs `HandbookRetriever.SearchAsync` (top-3 cosine), and hands back an `AIContext` whose messages get merged ahead of the input — the model sees the excerpts as a user-role context message before it is ever asked to answer. There is no tool call, no round trip, and no way for the model to skip retrieval; the flip side is it retrieves on every turn even when the question needs nothing from the corpus. Tool retrieval (Task 5) inverts the control: the model is handed a `search_handbook` tool and must choose to call it, paying an extra model round per lookup but keeping the model in charge of when grounding is needed. Task 5 builds the tool variant and compares the two empirically on the same scenarios.

**What the trace shows before the model call.** `Telemetry.Start("P04.HandbookRag")` was already wired (Program.cs line 6). One live run, scenario 1, trace `42c0f4c3b955ac9bf45e7a792a7c88db`: the first span the Agents instrumentation emits is `orchestrate_tools` (Internal, ActivitySource `Experimental.Microsoft.Agents.AI`), which immediately opens a `chat` Client child span (`gen_ai.operation.name: chat`, `gen_ai.request.model: glm-5.3-flash:cloud`, `server.address: localhost:11434`) — orchestrate started at 23:37:28.6216 and the chat span at 28.6245, i.e. the chat span is nested inside the orchestration span. The retriever's own work produces no activity at all: the query embed goes through OllamaSharp's `/api/embed` endpoint, which is not instrumented by the Agents source, so the embed-and-rank happens before/around these spans with no span of its own — it is visible in the trace only indirectly, through `gen_ai.usage.input_tokens` (382–393 tokens per turn vs a handful for the bare question: instructions + 3 injected chunks + question). So the honest reading of the trace is: the embedding call happens before the chat span, the context message is assembled from the retriever outside any emitted span, and the chat span's token counts are the only evidence the injection ran.

**Guardrail behavior.** All three scripted scenarios passed live on the first attempt — no instruction variants were needed, so the Task 4 Step 2 fallback loop never fired. The wording that held the fallback phrase is the original instruction block in `HandbookBot.Create` (src/P04.HandbookRag/HandbookBot.cs):

```
You are HelpDeskHQ's handbook bot. Answer ONLY from the provided handbook excerpts.
Cite the doc filename in square brackets, like [onboarding.md], for every fact you use.
If the excerpts do not answer the question, say exactly: 'That is not in the handbook.'
Do not use any knowledge that is not in the excerpts.
```

Transcripts (live, `dotnet run --project src/P04.HandbookRag`):

- "How many vacation days do I get?" → "You get **25 vacation days per year** [onboarding.md]. Additionally, up to 5 unused vacation days can roll over into the next calendar year; anything beyond that is forfeited on January 31 [onboarding.md]."
- "When must an RMA be filed?" → "An RMA must be filed within 14 days of the hardware failure. Requests filed late require a director's approval. [rma-hardware.md]"
- "What is the CEO's home address?" → "That is not in the handbook."

Note the third answer is exactly the mandated fallback phrase, character for character — the model did not invent an address, did not hedge ("I don't have..."), and did not cite. The two grounded answers cite the correct doc for each: vacation days from [onboarding.md], the 14-day RMA window from [rma-hardware.md] — retrieval routed each question to a different document.

**Doc drift — context-provider API (third instance).** The Microsoft doc page for context providers drifted from shipped 1.19.0, same as sessions (P03) and MCP before it. Verified-against-package shape: the provider overrides `ProvideAIContextAsync(AIContextProvider.InvokingContext context, CancellationToken cancellationToken)` — the invoking context is a nested type `AIContextProvider.InvokingContext`, the caller's messages are reached via `context.AIContext.Messages`, and the `AIContextProvider` base has a constructor with three optional parameters. This is recorded in the XML-doc comment on `HandbookContextProvider.ProvideAIContextAsync`. Running tally of doc-drift instances: sessions, MCP, providers. The pattern that has held three times: read `~/.nuget/packages/microsoft.agents.ai.abstractions/1.19.0` XML/reflection first, treat the doc page as secondary.
