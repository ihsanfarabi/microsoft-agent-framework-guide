# SPEC — P08: HarnessAgent (Overnight Ticket Batch)

**Tier:** Advanced · **Estimate:** 5–6 hours · **Depends on:** P07

## Goal

Replace the hand-wired P07 agent with the **Agent Harness** — one extension
method turns the Ollama chat client into a long-haul agent with todos, file
access, file memory, and built-in approvals. Story: overnight batch over the
backlog, kill mid-ticket, restart, agent picks up where it stopped.

## Concepts learned

- `AsHarnessAgent` + `HarnessAgentOptions` — one-method harness
- `FileAccessProvider` / `FileSystemAgentFileStore` — sandboxed `file_access_*` tools
- `FileMemoryProvider` — session-scoped durable memory (`agent-file-memory/{session}/`)
- Todo tracking in session state (multi-step planning across a batch)
- `ToolApprovalAgent` / `UseToolApproval` + `ApprovalRequiredAIFunction` — human gate on risky tools, "don't ask again" standing approvals via `AutoApprovalRules`
- Session state restore across process restarts

## Requirements

1. `src/P08.HarnessAgent` console referencing `MafDemo.Core` (FileTicketStore) + P02 ticket tools.
2. Harness agent over `OllamaChat.Create()` with `HarnessAgentOptions`: file access store at `work/`, instructions for batch triage.
3. `close_ticket` tool wrapped in `ApprovalRequiredAIFunction`; console approval prompt; auto-approval rule that whitelists read-only tools.
4. Batch scenario: agent works 5 seeded backlog tickets — plans via todos, consults handbook files, writes resolution notes, requests approval before closing.
5. Kill-and-resume: Ctrl-C mid-batch, restart with same session state, agent resumes (todos + file memory intact).
6. Approval policy logic + note formatting pure functions xUnit-tested.

## Success criteria

- Full batch run closes all 5 tickets, every close individually approved or covered by a standing approval.
- Todo progress visible in trace/file memory between tickets.
- Kill mid-ticket → restart → batch completes without redoing finished tickets.
- Approval policy unit tests pass.

## Stretch

- Rebuild harness pieces standalone on a plain `ChatClientAgent` (context provider + middleware only) to see what the harness automates.
- GitHub Copilot SDK backend comparison for shell/file ops.

## Resources

- Harness blog (API verified): https://devblogs.microsoft.com/agent-framework/agent-harness-working-with-your-data-safely
- BUILD 2026 harness announce: https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-at-build-2026-announce
- Session docs: https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/session