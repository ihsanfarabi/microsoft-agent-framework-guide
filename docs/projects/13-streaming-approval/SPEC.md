# SPEC — P13: StreamingApproval (streaming chat + mid-stream tool approval over HTTP)

**Tier:** Advanced · **Estimate:** 5–6 hours · **Depends on:** P05, P10

## Story

Stream an agent's answer over SSE from a self-hosted HTTP endpoint; when the
agent calls a sensitive tool (`DeleteTicket` / `EscalateTicket`), the stream
pauses with an `approval` event, a human approves/denies over a second request,
and the agent resumes — same session, same pending tool call.

## Success criteria

- `POST /conversations/{id}/messages` streams text deltas as SSE frames; on a gated tool call it emits one `event: approval` frame with `requestId|tool|args` and ends the body.
- `POST /approvals/{id}` body `{requestId, approved}` resumes the agent with the *same* session and tool call snapshot; response streams as SSE. Decline path produces a tool-result refusal the model narrates, not a crash.
- Session checkpoint persists per conversation; a second message in the same conversation has prior context.
- Client demo (`scripts/demo13.sh`, curl) shows the full round trip.
- OpenAI-compat layer limitation documented with source links.

## Key verified facts (P13 research)

- Agent-side approval round-trip works with streaming: `RunStreamingAsync` surfaces `ToolApprovalRequestContent { RequestId, ToolCall, RequiresConfirmation }` in `update.Contents`, the agent yields-break, and resume is
  `RunStreamingAsync(new ChatMessage(ChatRole.User, [request.CreateResponse(approved, reason)]), sameSession)` — rebound by request id against a recorded tool-call snapshot (see MAF `ToolApprovalAgent`). Wrap: `new ApprovalRequiredAIFunction(AIFunctionFactory.Create(...))`. Builder: `.UseToolApproval(new ToolApprovalAgentOptions{...})`.
- **This exact loop already runs in-repo**: `src/P08.HarnessAgent/Program.cs` `DriveAsync()` (surfacing, dedupe by call id, multi-request bursts, resume, `SerializeSessionAsync` checkpoints).
- Middleware approval fires during streaming too (MAF `RunStreamingAsync_WithFunctionCall_InvokesMiddlewareAsync`), but blocking prompts can never work cross-request — that's what the HTTP store replaces.
- **`MapOpenAIChatCompletions` silently drops approval content**: the content switch maps Text/FunctionCall and `_ => null` — a `ToolApprovalRequestContent` produces no SSE frame and there is no inbound channel for a response. Custom endpoint required. Document with: microsoft/agent-framework `AIAgentChatCompletionsProcessor.cs`.
- glm-5.3-flash bursts several gated calls per turn — every surfaced request must be answered; answer in order.

## Non-goals

No durable scheduler; pending-approval store is in-memory (restart with pending
approval = "turn died, ask again" — documented). No changes to P05/P07/P10.

## Resources

- https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval
- https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/openai-endpoints
- MAF source: `dotnet/src/Microsoft.Agents.AI/Harness/ToolApproval/ToolApprovalAgent.cs`, `Microsoft.Agents.AI.Hosting.OpenAI/ChatCompletions/AIAgentChatCompletionsProcessor.cs`
- AG-UI HITL: https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/ui/ag-ui/human-in-the-loop