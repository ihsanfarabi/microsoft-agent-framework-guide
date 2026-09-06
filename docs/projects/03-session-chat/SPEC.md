# SPEC — P03: SessionChat (Multi-Turn + Persistence)

**Tier:** Basic · **Estimate:** 4–5 hours · **Depends on:** P02

## Goal

Stateful REPL chatbot. Conversation remembers across turns and survives
process restarts via session serialization. Also adds `FileTicketStore` to the
shared core — tickets outlive the process too.

## Concepts learned

- `AgentSession`: `CreateSessionAsync`, `RunAsync(prompt, session)`
- Session serialization: `SerializeSession` / `DeserializeSessionAsync`
- Session state (`StateBag`) vs chat history vs context providers — what holds what
- `FileTicketStore` — domain persistence alongside conversation persistence

## Requirements

1. `MafDemo.Core` gains `FileTicketStore` (JSON file backing, same `ITicketStore` interface) — TDD.
2. `P03.SessionChat` console REPL with `TicketBot` tools (from P02) over `FileTicketStore`.
3. Commands: `/new` (fresh session), `/list` (saved sessions), `/switch <id>`, `/quit`.
4. Sessions serialized to `threads/` dir after every turn; restore on `/switch`.
5. OTel traces on (P01 pattern).

## Success criteria

- Turn 1: "my laptop model is LTX-2201". Turn 5: "what's my laptop model?" — answered LTX-2201.
- Create ticket turn 1, kill process, restart, `/switch`, ask "what was my last ticket?" — answered.
- Tickets persist in JSON file across restarts (`FileTicketStore`).
- Unit tests pass for `FileTicketStore` round-trip.

## Stretch

- Long-term memory: custom context provider storing one durable fact across *different* sessions (seed for P04 provider work).

## Resources

- Session: https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/session
- Conversations overview: https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations