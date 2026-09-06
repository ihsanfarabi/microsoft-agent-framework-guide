# P13 StreamingApproval Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Self-hosted SSE endpoint where a streaming agent pauses on gated tool calls, gets human approval over HTTP, and resumes with the same session.

**Architecture:** WebApplication hosting a `ChatClientAgent` with two `ApprovalRequiredAIFunction` tools over `FileTicketStore`. Two endpoints: message (SSE stream, stores pending pending-approval) and approval (resumes with persisted session). P08's proven streaming loop is the reference implementation; nothing here touches P08 code.

**Tech Stack:** `Microsoft.Agents.AI` 1.19.0, ASP.NET Core minimal APIs (SSE via `Results.Stream`), Ollama `glm-5.3-flash:cloud`, xUnit with a scripted fake `IChatClient`.

**Spec:** `docs/projects/13-streaming-approval/SPEC.md`

## Global Constraints

- Same as P11/P12: .NET 10; live tests gated `RUN_EVALS=1`; commits `type(p13): ...` + Co-Authored-By trailer; RTK git; no edits to `src/P01-P10`.
- Agent construction mirrors `src/P08.HarnessAgent/HarnessFacts.cs` minus the file-memory side.

---

### Task 1: Agent + streaming message endpoint

**Files:**
- Create: `src/P13.StreamingApproval/P13.StreamingApproval.csproj`, `src/P13.StreamingApproval/appsettings.json`, `src/P13.StreamingApproval/Agents/TicketAgent.cs`, `src/P13.StreamingApproval/PendingApprovals.cs`, `src/P13.StreamingApproval/Program.cs`
- Test: `tests/P13.StreamingApproval.Tests/SseContractTests.cs`

**Interfaces:**
- Consumes: `MafDemo.AgentCommon.OllamaChat.Create()`, `MafDemo.Core.Stores.FileTicketStore` (P05/P08 pattern), `MafDemo.Core.Stores.FileTicketStore` ticket API used by P08.
- Produces: `TicketAgent.Build(IChatClient)` → `ChatClientAgent` with `DeleteTicket`/`EscalateTicket` wrapped in `ApprovalRequiredAIFunction`, read-only auto-approve rules for list/query tools (`.UseToolApproval(new ToolApprovalAgentOptions{...})`); `PendingApprovals` store: `Add(ConversationId, requestId, request, session) → bool`, `Take(requestId) → (request, session)?`; endpoint `POST /conversations/{id}/messages` body `{"text": "..."}` → SSE.

- [x] **Step 1: scaffold** — csproj references AgentCommon + Core + `Microsoft.Agents.AI` 1.19.0 (copy csproj package block from `P08.HarnessAgent`); add to slnx. Suppress `MAAI001` experimental diag if `UseToolApproval` demands it (P08 already carries the NoWarn — mirror it).
- [x] **Step 2: failing SSE-contract test** — fake `IChatClient` scripted to yield text delta → `FunctionCallContent("DeleteTicket")`; assert the HTTP response content-type is `text/event-stream` and frames parse: first a delta frame, then an `event: approval` frame carrying a requestId. Shape (exact test code refined while writing the real `Program` types together in step 4):

```csharp
[Fact]
public async Task Message_stream_emits_approval_event_on_gated_call()
{
    var app = TestApp.Build(fakeClient: ScriptedClient.TextThenCall("DeleteTicket", """{"ticketId":"T1"}"""),
                             approvals: out var store);
    var response = await PostMessageAsync(app, "c1", "delete ticket T1");
    var body = await response.Content.ReadAsStringAsync();
    Assert.Contains("event: approval", body);
    Assert.Contains("DeleteTicket", body);
}
```
(If raw `WebApplication` test-host wiring fights, test the stream *writer* as a pure function over an `IAsyncEnumerable<AgentResponseUpdate>` — same contract, no Kestrel.)
- [x] **Step 3: run** `rtk dotnet test tests/P13.StreamingApproval.Tests` → FAIL.
- [x] **Step 4: implement** — `TicketAgent.Build` per P08 `HarnessFacts` shape; `Program.cs` endpoint: iterate `agent.RunStreamingAsync(new ChatMessage(user,text), session)`, forward text as `data: {"delta":…}` frames; on update containing `ToolApprovalRequestContent` → store `{conversationId, request, session}` in `PendingApprovals`, emit `event: approval {"requestId","tool","args"}`, end. Frame mapping in a plain method so the fake-client test runs offline.
- [x] **Step 5:** tests green + manual smoke `dotnet run` + script curl. Commit `feat(p13): approval-aware SSE message endpoint`.

### Task 2: Approval round trip (approve / decline / always-approve)

**Files:** Modify `src/P13.StreamingApproval/Program.cs`; Test: same file as Task 1.

- [x] **Step 1: failing test** — with the scripted fake, resume after `POST /approvals/{id}` `{approved:true}` produces a second SSE whose content includes the resumed tool result; decline path produces a narrated refusal frame (fake returns "denied" behavior via tool result content). 
- [x] **Step 2: implement endpoint**
```csharp
group.MapPost("/approvals/{conversationId}", async (string conversationId, ApprovalVote vote) =>
{
    var pending = approvals.Take(vote.RequestId)
        ?? Results.NotFound();  // return shape: TypedResults vs null — resolve at implementation
    var resume = new ChatMessage(ChatRole.User,
        [pending.Value.request.CreateResponse(vote.Approved, vote.Reason)]);
    // stream resumed turn exactly like /messages
});
```
Also support `{"approveAlways": true}` → `request.CreateAlwaysApproveToolResponse()`.
- [x] **Step 3:** tests green; live demo: approve once with real Ollama, decline once. Commit `feat(p13): approval round-trip with decline + always-approve`.

### Task 3: Session persistence + multi-turn + bursts

- [x] **Step 1:** checkpoint each conversation to disk after every stream ends (`SerializeSessionAsync` → `sessions/{id}.json`, P08 pattern); load on demand. `POST` second message must recall first message.
- [x] **Step 2:** guard the burst: `PendingApprovals` keyed by conversation must queue; demo answers every surfaced request (fake-client unit test: two `FunctionCallContent` frames → two approval events). If glm-5.3 actually bursts, record as NOTES.
- [x] **Step 3:** commit `feat(p13): conversation persistence + multi-request approvals`.

### Task 4: Client demo + docs

- [x] **Step 1:** `scripts/demo13.sh` — curl `-N` SSE message call, capture approval JSON, second curl posts the vote, print resumed text.
- [x] **Step 2:** `docs/projects/13-streaming-approval/NOTES.md` — OpenAI-compat layer drops approval content (link processor source), pending-request in-memory tradeoff, burst behavior of this model.
- [x] **Step 3:** README ladder + PORTFOLIO row; full suite green; commit `docs(p13): notes + portfolio entries`.