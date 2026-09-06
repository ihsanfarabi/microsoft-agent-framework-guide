# SPEC — P04: HandbookRag (Context Providers + Grounding)

**Tier:** Intermediate · **Estimate:** 5–6 hours · **Depends on:** P03

## Goal

Agent grounded in a private IT handbook corpus. Retrieval auto-injected via a
custom `AIContextProvider` — the MAF-native RAG pattern. First intermediate
project: retrieval quality now matters, not just plumbing.

## Concepts learned

- `AIContextProvider`: `ProvideAIContextAsync` / `StoreAIContextAsync`
- `ChatClientAgentOptions.AIContextProviders` wiring
- Local embeddings via Ollama (`nomic-embed-text`), cosine similarity, chunking
- Grounding instructions vs tool-based retrieval — two RAG styles
- Hallucination guardrail

## Requirements

1. Corpus: 10 markdown docs in `docs/corpus/` (vpn-policy, password-reset, rma-hardware, software-install-policy, wifi-setup, email-setup, security-incident, onboarding, license-management, backup-policy) — each with concrete, checkable facts (numbers, named procedures).
2. `HandbookChunker` + `HandbookRetriever` in `MafDemo.Core` — pure, TDD: chunker handles empty doc + oversized doc; retriever returns the right chunk for a keyword query against a fake embedder.
3. Real embedding via Ollama `nomic-embed-text` (`ollama pull nomic-embed-text` — local, free; glm-5.3-flash:cloud does not serve embeddings).
4. `HandbookContextProvider : AIContextProvider` — injects top-3 chunks before each run.
5. Agent answers from corpus, cites doc by filename.
6. Guardrail: question absent from corpus → "not in the handbook" answer, no hallucinated fact.

## Success criteria

- "How many vacation days do I get?" (in onboarding.md) answered with the exact number + doc cited.
- Trace/messages show injected context before the model call.
- Guardrail question ("What's the CEO's home address?") refused from corpus.
- Chunker/retriever unit tests pass.

## Stretch

- Tool-based variant: `search_handbook` function tool; compare model behavior vs auto-injection (when does each win).
- Swap local embeddings for a vector index (FAISS/AI Search style) — forward link to P10.

## Resources

- Context providers: https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/context-providers
- RAG: https://learn.microsoft.com/en-us/agent-framework/agents/rag