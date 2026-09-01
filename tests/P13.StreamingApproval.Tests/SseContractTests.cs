using System.Text;
using System.Text.Json;
using MafDemo.Core.Domain;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using P13.StreamingApproval;
using P13.StreamingApproval.Agents;

namespace P13.StreamingApproval.Tests;

/// <summary>
/// SSE contract of the approval-aware message stream, exercised offline: a
/// scripted fake <see cref="IChatClient"/> (P11's ScriptedClient pattern,
/// streaming form) plays a text delta followed by a model tool call, and the
/// tests assert what a client sees — text deltas as <c>data:</c> frames and a
/// gated <c>delete_ticket</c> call as an <c>event: approval</c> frame carrying
/// a requestId — with the request parked in <see cref="PendingApprovals"/> for
/// the (later-task) resume endpoint. The WebApplicationFactory tests go
/// through the real endpoint (content-type included) with the DI chat client
/// swapped for the fake, so no Ollama is needed.
/// </summary>
public class SseContractTests
{
    [Fact]
    public async Task Frame_writer_emits_delta_then_approval_event_on_gated_call()
    {
        var agent = TicketAgent.Build(new ScriptedClient(ScriptedClient.TextThenDelete), NewStore());
        var session = await agent.CreateSessionAsync();
        var approvals = new PendingApprovals();

        var body = await RenderAsync(agent, session, approvals);

        var deltaAt = body.IndexOf("data: {\"delta\":", StringComparison.Ordinal);
        var approvalAt = body.IndexOf("event: approval", StringComparison.Ordinal);
        Assert.True(deltaAt >= 0, "no delta frame emitted");
        Assert.True(approvalAt > deltaAt, "approval frame must come after the delta");
        Assert.Contains("\"tool\":\"delete_ticket\"", body);
    }

    [Fact]
    public async Task Approval_frame_requestid_parks_pending_request_for_resume()
    {
        var agent = TicketAgent.Build(new ScriptedClient(ScriptedClient.TextThenDelete), NewStore());
        var session = await agent.CreateSessionAsync();
        var approvals = new PendingApprovals();

        var body = await RenderAsync(agent, session, approvals);

        // The frame carries a requestId, and quoting it back to the store
        // hands over exactly one pending request bound to the paused session
        // (the resume endpoint's contract) — and it is consumed by the take.
        var payload = body.Split("event: approval\ndata: ", 2)[1].Split("\n\n", 2)[0];
        using var doc = JsonDocument.Parse(payload);
        var requestId = doc.RootElement.GetProperty("requestId").GetString();
        Assert.False(string.IsNullOrEmpty(requestId));
        Assert.True(approvals.TryTake(requestId!, out var pending));
        Assert.Equal("c1", pending!.ConversationId);
        Assert.Same(session, pending.Session);
        Assert.Equal("delete_ticket", (pending.Request.ToolCall as FunctionCallContent)?.Name);
        Assert.False(approvals.TryTake(requestId!, out _));
    }

    [Fact]
    public async Task Read_only_tool_runs_unapproved_and_emits_no_approval_event()
    {
        var store = NewStore();
        var agent = TicketAgent.Build(new ScriptedClient(ScriptedClient.TextThenList, ScriptedClient.FinalText),
            store);
        var session = await agent.CreateSessionAsync();
        var approvals = new PendingApprovals();

        var body = await RenderAsync(agent, session, approvals);

        Assert.DoesNotContain("event: approval", body);
        Assert.Equal(0, approvals.Count);
        // The tool really ran through the auto-approval rule: the scripted
        // second call only happens after the tool result returns to the model.
        Assert.Contains("data: {\"delta\":", body);
    }

    [Fact]
    public async Task Continuation_of_paused_session_without_approval_emits_error_frame()
    {
        // The approval protocol pairs every surfaced request with a response;
        // a continuation message on a paused session makes the harness refuse
        // the run (observed live as a 500 before the error-frame handling) —
        // the endpoint turns that into an SSE error frame.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(new ScriptedClient(ScriptedClient.TextThenDelete))));
        var client = factory.CreateClient();

        var conversationId = $"c2-{Guid.NewGuid():N}";
        using var first = new StringContent("""{"text":"delete the broken ticket"}""", Encoding.UTF8, "application/json");
        using var firstResponse = await client.PostAsync($"/conversations/{conversationId}/messages", first);
        Assert.Contains("event: approval", await firstResponse.Content.ReadAsStringAsync());

        using var second = new StringContent("""{"text":"just list them"}""", Encoding.UTF8, "application/json");
        using var secondResponse = await client.PostAsync($"/conversations/{conversationId}/messages", second);
        var body = await secondResponse.Content.ReadAsStringAsync();
        Assert.Contains("event: error", body);
        Assert.Contains("approval-required", body);
    }

    [Fact]
    public async Task Message_endpoint_responds_text_event_stream_with_approval_frame()
    {
        var approvals = new PendingApprovals();
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(new ScriptedClient(ScriptedClient.TextThenDelete))));
        // The app's PendingApprovals singleton is the one the endpoint parked
        // the request in — resolve it rather than handing in our own.
        var client = factory.CreateClient();

        var conversationId = $"c1-{Guid.NewGuid():N}";
        using var content = new StringContent("""{"text":"delete that ticket"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/conversations/{conversationId}/messages", content);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var deltaAt = body.IndexOf("data: {\"delta\":", StringComparison.Ordinal);
        var approvalAt = body.IndexOf("event: approval", StringComparison.Ordinal);
        Assert.True(deltaAt >= 0, $"no delta frame in: {body}");
        Assert.True(approvalAt > deltaAt, "approval frame must come after the delta");
        Assert.Contains("delete_ticket", body);

        var endpointStore = factory.Services.GetRequiredService<PendingApprovals>();
        Assert.Equal(1, endpointStore.Count);
    }

    [Fact]
    public async Task Failed_resume_re_parks_the_approval_and_emits_error_frame()
    {
        // The T2-review minor, addressed: a resume that THROWS mid-run must
        // not orphan the approval — the taken request goes back into the
        // store and the failure surfaces as an SSE error frame (never a
        // 500), so the operator can answer it again.
        var store = NewStore();
        var ticket = await store.CreateAsync("Broken printer", "Dead on arrival", TicketPriority.High);
        var scripted = new ScriptedClient(ScriptedClient.DeleteTicketTurn("call-1", ticket.Id.ToString()));
        using var factory = FactoryWith(new ThrowOnSecondCallClient(scripted), store);
        var client = factory.CreateClient();
        var approvals = factory.Services.GetRequiredService<PendingApprovals>();

        var conversationId = $"failR-{Guid.NewGuid():N}";
        using (var message = new StringContent("""{"text":"delete the broken ticket"}""", Encoding.UTF8,
                   "application/json"))
        using (var first = await client.PostAsync($"/conversations/{conversationId}/messages", message))
        {
            Assert.Contains("event: approval", await first.Content.ReadAsStringAsync());
        }

        var requestId = approvals.PendingRequestIds(conversationId).Single();
        using (var vote = new StringContent($$"""{"requestId":"{{requestId}}","approved":true}""",
                   Encoding.UTF8, "application/json"))
        using (var resume = await client.PostAsync($"/approvals/{conversationId}", vote))
        {
            var body = await resume.Content.ReadAsStringAsync();
            Assert.Contains("event: error", body);
            Assert.Contains("resume-failed", body);
        }

        // The approval is back in the store, unspoiled, for another try.
        Assert.Equal(1, approvals.Count);
        Assert.True(approvals.TryTake(requestId, out var reParked));
        Assert.Equal("delete_ticket", (reParked!.Request.ToolCall as FunctionCallContent)?.Name);
        // P08's resume hazard, now observable: the harness executes the call
        // BEFORE the (throwing) model call, so the re-parked approval points
        // at an already-executed action — re-answering it resumes the session
        // and the harness finds no matching pending call (an error, never a
        // double execution). The re-park is best-effort, not a rewind.
        Assert.DoesNotContain(ticket.Id, (await store.ListAsync()).Select(t => t.Id));
    }

    [Fact]
    public async Task Approve_resume_executes_tool_and_streams_resumed_turn()
    {
        // A real ticket in the store, and a script whose gated call targets
        // exactly that id: the resumed turn may only reach the second script
        // (the post-tool-result narration) if the parked request was answered
        // and the delete actually executed against the store.
        var store = NewStore();
        var ticket = await store.CreateAsync("Broken printer", "Dead on arrival", TicketPriority.High);
        using var factory = FactoryWith(
            new ScriptedClient(ScriptedClient.DeleteTicketTurn("call-1", ticket.Id.ToString()),
                ScriptedClient.DeletedText),
            store);
        var client = factory.CreateClient();

        using var message = new StringContent("""{"text":"delete the broken ticket"}""", Encoding.UTF8,
            "application/json");
        var conversationId = $"cA-{Guid.NewGuid():N}";
        using var first = await client.PostAsync($"/conversations/{conversationId}/messages", message);
        var firstBody = await first.Content.ReadAsStringAsync();
        var requestId = ExtractRequestId(firstBody);
        Assert.Contains(ticket.Id, (await store.ListAsync()).Select(t => t.Id)); // nothing deleted while paused

        using var vote = new StringContent(
            $$"""{"requestId":"{{requestId}}","approved":true,"reason":"operator approved"}""", Encoding.UTF8,
            "application/json");
        using var resume = await client.PostAsync($"/approvals/{conversationId}", vote);

        Assert.Equal("text/event-stream", resume.Content.Headers.ContentType?.MediaType);
        var resumed = await resume.Content.ReadAsStringAsync();
        Assert.Contains("data: {\"delta\":", resumed);
        Assert.Contains("Deleted the ticket.", resumed);
        Assert.DoesNotContain("event: approval", resumed);
        Assert.DoesNotContain(ticket.Id, (await store.ListAsync()).Select(t => t.Id)); // the tool really ran
        Assert.Equal(0, factory.Services.GetRequiredService<PendingApprovals>().Count);
    }

    [Fact]
    public async Task Decline_resume_narrates_refusal_and_leaves_store_intact()
    {
        var store = NewStore();
        var ticket = await store.CreateAsync("Broken printer", "Dead on arrival", TicketPriority.High);
        using var factory = FactoryWith(
            new ScriptedClient(ScriptedClient.DeleteTicketTurn("call-1", ticket.Id.ToString()),
                ScriptedClient.RefusalText),
            store);
        var client = factory.CreateClient();

        using var message = new StringContent("""{"text":"delete the broken ticket"}""", Encoding.UTF8,
            "application/json");
        var conversationId = $"cD-{Guid.NewGuid():N}";
        using var first = await client.PostAsync($"/conversations/{conversationId}/messages", message);
        var requestId = ExtractRequestId(await first.Content.ReadAsStringAsync());

        using var vote = new StringContent(
            $$"""{"requestId":"{{requestId}}","approved":false,"reason":"operator declined"}""", Encoding.UTF8,
            "application/json");
        using var resume = await client.PostAsync($"/approvals/{conversationId}", vote);
        var resumed = await resume.Content.ReadAsStringAsync();

        // The refusal is narrated as a delta frame — the model relays the
        // denial the harness handed back as the tool outcome.
        Assert.Contains("the operator declined", resumed);
        Assert.DoesNotContain("event: approval", resumed);
        // And the store is untouched: a decline never reaches the tool body.
        Assert.Contains(ticket.Id, (await store.ListAsync()).Select(t => t.Id));
    }

    [Fact]
    public async Task ApproveAlways_resume_marks_policy_and_autoapproves_next_gated_call()
    {
        var store = NewStore();
        var ticket = await store.CreateAsync("Broken printer", "Dead on arrival", TicketPriority.High);
        // Turn 2 gates the SAME tool again: with the standing rule recorded by
        // the always-approve response, this call must auto-pass (no second
        // approval event) and the run advances to turn 3's closing text.
        using var factory = FactoryWith(
            new ScriptedClient(ScriptedClient.DeleteTicketTurn("call-1", ticket.Id.ToString()),
                ScriptedClient.RepeatDeleteTurn(ticket.Id.ToString()), ScriptedClient.DoneText),
            store);
        var client = factory.CreateClient();

        using var message = new StringContent("""{"text":"delete the broken ticket"}""", Encoding.UTF8,
            "application/json");
        var conversationId = $"cE-{Guid.NewGuid():N}";
        using var first = await client.PostAsync($"/conversations/{conversationId}/messages", message);
        var requestId = ExtractRequestId(await first.Content.ReadAsStringAsync());

        using var vote = new StringContent($$"""{"requestId":"{{requestId}}","approveAlways":true}""",
            Encoding.UTF8, "application/json");
        using var resume = await client.PostAsync($"/approvals/{conversationId}", vote);
        var resumed = await resume.Content.ReadAsStringAsync();

        Assert.DoesNotContain("event: approval", resumed);
        Assert.Contains("All done.", resumed);
        Assert.DoesNotContain(ticket.Id, (await store.ListAsync()).Select(t => t.Id));
        Assert.Equal(0, factory.Services.GetRequiredService<PendingApprovals>().Count);
    }

    [Fact]
    public async Task Resumed_turn_can_pause_again_on_a_new_gated_call()
    {
        // Loop safety: the resumed run must be able to pause AGAIN — the same
        // parking + framing shape as /messages, so the client answers a new
        // requestId with another /approvals post (no recursion server-side).
        var store = NewStore();
        var ticket = await store.CreateAsync("Broken printer", "Dead on arrival", TicketPriority.High);
        using var factory = FactoryWith(
            new ScriptedClient(ScriptedClient.DeleteTicketTurn("call-1", ticket.Id.ToString()),
                ScriptedClient.DeleteThenEscalateTurn(ticket.Id.ToString())),
            store);
        var client = factory.CreateClient();

        using var message = new StringContent("""{"text":"delete the broken ticket"}""", Encoding.UTF8,
            "application/json");
        var conversationId = $"cF-{Guid.NewGuid():N}";
        using var first = await client.PostAsync($"/conversations/{conversationId}/messages", message);
        var requestId = ExtractRequestId(await first.Content.ReadAsStringAsync());

        using var vote = new StringContent($$"""{"requestId":"{{requestId}}","approved":true}""", Encoding.UTF8,
            "application/json");
        using var resume = await client.PostAsync($"/approvals/{conversationId}", vote);
        var resumed = await resume.Content.ReadAsStringAsync();

        var approvalAt = resumed.IndexOf("event: approval", StringComparison.Ordinal);
        Assert.True(approvalAt >= 0, "resumed turn surfacing a new gated call must emit another approval event");
        Assert.Contains("\"tool\":\"escalate_ticket\"", resumed);
        var newRequestId = ExtractRequestId(resumed);
        Assert.NotEqual(requestId, newRequestId);

        var approvals = factory.Services.GetRequiredService<PendingApprovals>();
        Assert.Equal(1, approvals.Count);
        Assert.True(approvals.TryTake(newRequestId, out var pending));
        Assert.Equal("escalate_ticket", (pending!.Request.ToolCall as FunctionCallContent)?.Name);
    }

    [Fact]
    public async Task Approvals_endpoint_returns_error_frame_for_unknown_request_id()
    {
        using var factory = FactoryWith(
            new ScriptedClient(ScriptedClient.DeleteTicketTurn("call-1", "00000000-0000-0000-0000-000000000009")));
        var client = factory.CreateClient();

        using var vote = new StringContent("""{"requestId":"no-such-request","approved":true}""", Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync($"/approvals/cZ-{Guid.NewGuid():N}", vote);

        // Task 3's restart contract: never a 500, and not a bare status
        // either — an SSE `event: error` frame naming the problem and the
        // recovery ("the parked turn died with the process — ask again").
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: error", body);
        Assert.Contains("unknown-request-id", body);
        Assert.Contains("ask again", body);
    }

    [Fact]
    public async Task Second_message_in_same_conversation_reflects_the_first()
    {
        // Multi-turn memory: the SAME session continues, so the second
        // model call must receive turn one's history — the probe is what
        // the fake client was actually sent, not what it parrots back.
        var store = NewStore();
        var scripted = new ScriptedClient(
            ScriptedClient.TextTurn("Codeword set: BANANA.\n"),
            ScriptedClient.TextTurn("Recalling from this conversation: BANANA.\n"));
        using var factory = FactoryWith(scripted, store);
        var client = factory.CreateClient();

        using var first = new StringContent("""{"text":"The codeword is BANANA. Remember it."}""",
            Encoding.UTF8, "application/json");
        await client.PostAsync("/conversations/mem1/messages", first);

        using var second = new StringContent("""{"text":"What is the codeword?"}""",
            Encoding.UTF8, "application/json");
        await client.PostAsync("/conversations/mem1/messages", second);

        Assert.True(scripted.SeenMessages.Count >= 2, "the fake client must have been called twice");
        var secondCall = scripted.SeenMessages[1];
        Assert.Contains(secondCall, m => m.Role == ChatRole.User && m.Text.Contains("The codeword is BANANA."));
        Assert.Contains(secondCall, m => m.Role == ChatRole.Assistant && m.Text.Contains("Codeword set: BANANA."));
    }

    [Fact]
    public async Task Checkpoint_after_stream_end_rehydrates_in_a_new_host()
    {
        // Restart persistence: the checkpoint file written at the end of
        // turn one must be picked up by a FRESH host (new DI container =
        // empty ConversationSessions), and the loaded session must carry
        // turn one into turn two. This is P08's kill-and-resume, per
        // conversation.
        var conversationId = $"persist-{Guid.NewGuid():N}";
        var checkpointPath = CheckpointPath(conversationId);
        Assert.True(!File.Exists(checkpointPath), "test precondition: fresh conversation id");

        var store = NewStore();
        using (var first = FactoryWith(
                   new ScriptedClient(ScriptedClient.TextTurn("Codeword set: BANANA.\n")), store))
        {
            var client = first.CreateClient();
            using var content = new StringContent("""{"text":"The codeword is BANANA. Remember it."}""",
                Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"/conversations/{conversationId}/messages", content);
            Assert.Contains("data: {\"delta\":", await response.Content.ReadAsStringAsync());
        }
        Assert.True(File.Exists(checkpointPath),
            $"stream end must have checkpointed {checkpointPath}");

        // The "restart": a brand-new factory — nothing of the first host's
        // ConversationSessions survives; only the JSON file on disk.
        var scripted = new ScriptedClient(ScriptedClient.TextTurn("From restored memory: BANANA.\n"));
        using (var second = FactoryWith(scripted, store))
        {
            var client = second.CreateClient();
            using var content = new StringContent("""{"text":"What is the codeword?"}""",
                Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"/conversations/{conversationId}/messages", content);
            Assert.Contains("From restored memory", await response.Content.ReadAsStringAsync());
        }

        // The restored session's history contains turn one — the second
        // request saw it, which is the round-trip proof (serialize ->
        // deserialize -> the model gets the old turns).
        var firstCallAfterRestart = Assert.Single(scripted.SeenMessages);
        Assert.Contains(firstCallAfterRestart, m => m.Role == ChatRole.User
            && m.Text.Contains("The codeword is BANANA."));
        Assert.Contains(firstCallAfterRestart, m => m.Role == ChatRole.Assistant
            && m.Text.Contains("Codeword set: BANANA."));
    }

    [Fact]
    public async Task Burst_turn_parks_every_gated_call_and_answers_them_in_order()
    {
        // Task 3 burst guard, against MAF 1.19's ACTUAL surfacing behavior
        // (verified with this exact script against the real middleware): a
        // turn asking for BOTH destructive tools surfaces ONE request per
        // run — the agent-level approval middleware ends the run at the
        // first gated call — and the RESUMED run surfaces the next one. So
        // the burst parks one request per pause round, each with its own
        // requestId, and answering them in order executes the whole burst.
        // Resuming the first NEVER auto-consumes the second.
        var store = NewStore();
        var ticket = await store.CreateAsync("Burst probe", "Two gated calls at once", TicketPriority.High);
        using var factory = FactoryWith(
            new ScriptedClient(ScriptedClient.BurstTurn(ticket.Id.ToString()), ScriptedClient.BurstDoneText),
            store);
        var client = factory.CreateClient();
        var approvals = factory.Services.GetRequiredService<PendingApprovals>();

        // Turn 1: the burst surfaces its FIRST request; the store is
        // untouched while it waits.
        var conversationId = $"cBurst-{Guid.NewGuid():N}";
        using (var message = new StringContent("""{"text":"delete and escalate that ticket"}""",
                   Encoding.UTF8, "application/json"))
        using (var first = await client.PostAsync($"/conversations/{conversationId}/messages", message))
        {
            var firstBody = await first.Content.ReadAsStringAsync();
            var rid1 = Assert.Single(ExtractRequestIds(firstBody));
            Assert.Equal(1, approvals.Count);
            Assert.Equal("delete_ticket",
                (Parked(approvals, rid1)!.Request.ToolCall as FunctionCallContent)?.Name);
            Assert.Contains(ticket.Id, (await store.ListAsync()).Select(t => t.Id));
        }

        var firstRid = approvals.PendingRequestIds(conversationId).Single();
        using (var vote1 = new StringContent($$"""{"requestId":"{{firstRid}}","approved":true}""",
                   Encoding.UTF8, "application/json"))
        using (var resume1 = await client.PostAsync($"/approvals/{conversationId}", vote1))
        {
            var resume1Body = await resume1.Content.ReadAsStringAsync();
            // The resumed run surfaces the burst's SECOND request: a fresh,
            // DISTINCT approval event — the first answer did not consume it.
            var rid2 = Assert.Single(ExtractRequestIds(resume1Body));
            Assert.NotEqual(firstRid, rid2);
            Assert.Equal(1, approvals.Count);
            Assert.Equal([rid2], approvals.PendingRequestIds(conversationId));
            Assert.Equal("escalate_ticket",
                (Parked(approvals, rid2)!.Request.ToolCall as FunctionCallContent)?.Name);
            // And the harness held the whole burst: answering the first did
            // not half-execute it — the delete has still not run.
            Assert.Contains(ticket.Id, (await store.ListAsync()).Select(t => t.Id));
        }

        // Answering the second executes the whole burst.
        var rid2b = approvals.PendingRequestIds(conversationId).Single();
        using (var vote2 = new StringContent($$"""{"requestId":"{{rid2b}}","approved":true}""",
                   Encoding.UTF8, "application/json"))
        using (var resume2 = await client.PostAsync($"/approvals/{conversationId}", vote2))
        {
            Assert.Contains("Both done.", await resume2.Content.ReadAsStringAsync());
        }

        Assert.Equal(0, approvals.Count);
        Assert.Empty(approvals.PendingRequestIds(conversationId));
        // The delete really ran: the ticket is tombstoned out of the store.
        Assert.DoesNotContain(ticket.Id, (await store.ListAsync()).Select(t => t.Id));
        Assert.Null(await store.GetAsync(ticket.Id));
    }

    [Fact]
    public async Task SseWriter_parks_and_frames_every_approval_request_in_one_update()
    {
        // The TRUE multi-request burst (P08's glm-5.3 shape: several gated
        // calls riding in ONE update) — the writer + store must park and
        // frame EVERY one, never dropping the burst down to a single frame.
        // The agent-level middleware in this MAF build surfaces one request
        // per run (see Burst_turn_...), so this pure form is how a
        // multi-request update is exercised offline.
        var agent = TicketAgent.Build(new ScriptedClient(ScriptedClient.TextThenDelete), NewStore());
        var session = await agent.CreateSessionAsync();
        var approvals = new PendingApprovals();
        var update = new AgentResponseUpdate(ChatRole.Assistant,
        [
            new ToolApprovalRequestContent("req-1", new FunctionCallContent("call-1", "delete_ticket",
                new Dictionary<string, object?> { ["id"] = "00000000-0000-0000-0000-000000000001" })),
            new ToolApprovalRequestContent("req-2", new FunctionCallContent("call-2", "escalate_ticket",
                new Dictionary<string, object?> { ["id"] = "00000000-0000-0000-0000-000000000001" })),
        ]);

        var frames = SseWriter.FramesFor(update, "cBurst", session, approvals);

        Assert.Equal(2, frames.Count(f => f.StartsWith("event: approval", StringComparison.Ordinal)));
        Assert.Equal(2, approvals.Count);
        Assert.Equal(["req-1", "req-2"], approvals.PendingRequestIds("cBurst"));
        Assert.Equal("req-1", ExtractRequestId(string.Concat(frames)));
        Assert.True(approvals.TryTake("req-2", out var second));
        Assert.Equal("escalate_ticket", (second!.Request.ToolCall as FunctionCallContent)?.Name);
        // Taking the second left the first parked — answers are per-request.
        Assert.Equal(["req-1"], approvals.PendingRequestIds("cBurst"));
    }

    [Fact]
    public void PendingApprovals_take_consumes_one_and_leaves_the_rest_queued()
    {
        var approvals = new PendingApprovals();
        approvals.Add("cQ", new ToolApprovalRequestContent("req-1",
            new FunctionCallContent("call-1", "delete_ticket", new Dictionary<string, object?>())), null!);
        approvals.Add("cQ", new ToolApprovalRequestContent("req-2",
            new FunctionCallContent("call-2", "escalate_ticket", new Dictionary<string, object?>())), null!);

        Assert.Equal(["req-1", "req-2"], approvals.PendingRequestIds("cQ"));
        Assert.True(approvals.TryTake("req-1", out var taken));
        Assert.Equal("req-1", taken!.RequestId);
        Assert.Equal(1, approvals.Count);
        Assert.Equal(["req-2"], approvals.PendingRequestIds("cQ"));
        Assert.True(approvals.TryTake("req-2", out _));
        Assert.Equal(0, approvals.Count);
        Assert.Empty(approvals.PendingRequestIds("cQ"));
        Assert.False(approvals.TryTake("req-1", out _)); // one decision consumes one entry
    }

    [Fact]
    public async Task Resume_for_requestId_after_restart_returns_error_frame_not_500()
    {
        // Restart contract: a request parked before the restart is gone
        // (PendingApprovals is in-memory; the disk checkpoint keeps only the
        // session history). Resuming the dead requestId must yield the
        // documented error frame — "the turn died, ask again" — never a 500.
        var conversationId = $"restart-{Guid.NewGuid():N}";
        string parkedRequestId;
        var store = NewStore();
        using (var first = FactoryWith(new ScriptedClient(ScriptedClient.TextThenDelete), store))
        {
            var client = first.CreateClient();
            using var content = new StringContent("""{"text":"delete the broken ticket"}""", Encoding.UTF8,
                "application/json");
            var response = await client.PostAsync($"/conversations/{conversationId}/messages", content);
            parkedRequestId = ExtractRequestId(await response.Content.ReadAsStringAsync());
        } // host stopped: in-memory PendingApprovals gone, checkpoint remains

        using var second = FactoryWith(new ScriptedClient(ScriptedClient.TextThenDelete), store);
        var resumedClient = second.CreateClient();

        using var vote = new StringContent($$"""{"requestId":"{{parkedRequestId}}","approved":true}""",
            Encoding.UTF8, "application/json");
        using var response2 = await resumedClient.PostAsync($"/approvals/{conversationId}", vote);

        Assert.Equal(System.Net.HttpStatusCode.OK, response2.StatusCode);
        var body = await response2.Content.ReadAsStringAsync();
        Assert.Equal("text/event-stream", response2.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: error", body);
        Assert.Contains("unknown-request-id", body);
        Assert.Contains("do not survive a restart", body);
        Assert.Contains("ask again", body);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>A factory whose chat client is the scripted fake and whose
    /// ticket store is the caller's own file-backed one — so the gated call's
    /// ticket id can be a real, pre-created ticket and the store's tombstone
    /// state is directly assertable.</summary>
    private static WebApplicationFactory<Program> FactoryWith(IChatClient client,
        DeletableTicketStore? store = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient>(client);
            if (store is not null) services.AddSingleton(store);
        }));

    /// <summary>Pulls the requestId out of a body's first
    /// <c>event: approval</c> frame — the round-trip correlation id.</summary>
    private static string ExtractRequestId(string body)
    {
        var ids = ExtractRequestIds(body);
        return ids.Count > 0 ? ids[0]
            : throw new InvalidOperationException("body carried no approval frame: " + body);
    }

    /// <summary>Every requestId carried by the body's <c>event: approval</c>
    /// frames, in stream order — the burst probe reads ALL of them.</summary>
    private static List<string> ExtractRequestIds(string body)
    {
        const string marker = "event: approval\ndata: ";
        var ids = new List<string>();
        for (var at = body.IndexOf(marker, StringComparison.Ordinal);
             at >= 0;
             at = body.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            var payload = body[(at + marker.Length)..].Split("\n\n", 2)[0];
            using var doc = JsonDocument.Parse(payload);
            ids.Add(doc.RootElement.GetProperty("requestId").GetString()
                    ?? throw new InvalidOperationException("approval frame carried no requestId"));
        }

        return ids;
    }

    /// <summary>Inspects a parked request without answering it — take and
    /// put straight back (the take held the only claim, so the put-back is
    /// lossless; the queue dedups the re-filed id on read).</summary>
    private static PendingApproval Parked(PendingApprovals approvals, string requestId)
    {
        Assert.True(approvals.TryTake(requestId, out var pending),
            $"requestId '{requestId}' should still be parked");
        approvals.Add(pending!.ConversationId, pending.Request, pending.Session);
        return pending;
    }

    /// <summary>The checkpoint file the app writes for a conversation — the
    /// same path construction as Program's <c>ConversationSessions</c>
    /// (test-bin work dir), so restart tests can assert the file directly.</summary>
    private static string CheckpointPath(string conversationId) =>
        Path.Combine(AppContext.BaseDirectory, "work", "sessions", $"{conversationId}.json");

    /// <summary>A fresh file-backed store in a throwaway directory — the
    /// same concrete store production runs, so the tool bodies run against
    /// real files.</summary>
    private static DeletableTicketStore NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "p13-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new DeletableTicketStore(
            Path.Combine(dir, "tickets.json"),
            Path.Combine(dir, "tickets-deleted.json"));
    }

    private static async Task<string> RenderAsync(AIAgent agent, AgentSession session,
        PendingApprovals approvals)
    {
        var writer = new StringWriter();
        await SseWriter.WriteAsync(
            writer,
            agent.RunStreamingAsync(new ChatMessage(ChatRole.User, "delete the broken ticket"), session),
            "c1",
            session,
            approvals);
        return writer.ToString();
    }

}

/// <summary>
/// Wraps a <see cref="ScriptedClient"/> and THROWS on the second model call —
/// the resume-failure probe: the first turn parks its approval normally, the
/// resumed run dies mid-stream, and the test asserts the approval was
/// re-parked (the T2-review minor) instead of being silently consumed.
/// </summary>
public sealed class ThrowOnSecondCallClient(ScriptedClient inner) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("P13's SSE path is streaming only");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (inner.SeenMessages.Count >= 1)
            throw new InvalidOperationException("simulated mid-resume failure");
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// Streaming fake <see cref="IChatClient"/> (P11's ScriptedClient shape):
/// each GetStreamingResponseAsync call replays the next scripted update list
/// (the last one repeats). The scripts mirror what the model puts on the
/// wire — text deltas and FunctionCallContent — so everything downstream
/// (FunctionInvokingChatClient, the approval middleware, the SSE writer)
/// runs for real.
/// </summary>
public sealed class ScriptedClient(params ChatResponseUpdate[][] scripts) : IChatClient
{
    private int _index;

    /// <summary>Every request's message list, in call order — the Task-3
    /// multi-turn probe: the second call's messages are what the session
    /// actually remembered from the first turn.</summary>
    public List<IReadOnlyList<ChatMessage>> SeenMessages { get; } = new();

    /// <summary>One turn: a text delta, then the model asking to delete a
    /// ticket. Enough to trip the approval gate.</summary>
    public static readonly ChatResponseUpdate[] TextThenDelete =
    [
        new(ChatRole.Assistant, "Deleting that ticket for you.\n"),
        new(ChatRole.Assistant, [new FunctionCallContent("call-1", "delete_ticket",
            new Dictionary<string, object?> { ["id"] = "9d2f1e3c-0000-0000-0000-000000000001" })]),
    ];

    /// <summary>First turn of the read-only script: ask to list tickets.</summary>
    public static readonly ChatResponseUpdate[] TextThenList =
    [
        new(ChatRole.Assistant, "Let me check the tickets.\n"),
        new(ChatRole.Assistant, [new FunctionCallContent("call-2", "list_tickets",
            new Dictionary<string, object?>())]),
    ];

    /// <summary>Second turn of the read-only script: report the tool result
    /// and stop — reached only if the list_tickets call actually executed
    /// through the auto-approval rule.</summary>
    public static readonly ChatResponseUpdate[] FinalText =
    [
        new(ChatRole.Assistant, "There are no tickets.\n"),
    ];

    /// <summary>Post-resume narration after the delete tool result returns —
    /// reached only when the parked approval was answered and the call really
    /// executed.</summary>
    public static readonly ChatResponseUpdate[] DeletedText =
    [
        new(ChatRole.Assistant, "Deleted the ticket.\n"),
    ];

    /// <summary>Post-resume narration after a DECLINED approval — the harness
    /// hands the refusal back as the tool outcome and the model relays it.</summary>
    public static readonly ChatResponseUpdate[] RefusalText =
    [
        new(ChatRole.Assistant, "Understood - I won't delete the ticket; the operator declined.\n"),
    ];

    /// <summary>Closing text after a second gated call auto-passes — the
    /// approveAlways proof that the run advanced instead of pausing again.</summary>
    public static readonly ChatResponseUpdate[] DoneText =
    [
        new(ChatRole.Assistant, "All done.\n"),
    ];

    /// <summary>Builds the opening turn: a text delta then a gated
    /// delete_ticket call against the given ticket id.</summary>
    public static ChatResponseUpdate[] DeleteTicketTurn(string callId, string ticketId) =>
    [
        new(ChatRole.Assistant, "Deleting that ticket for you.\n"),
        new(ChatRole.Assistant, [new FunctionCallContent(callId, "delete_ticket",
            new Dictionary<string, object?> { ["id"] = ticketId })]),
    ];

    /// <summary>A follow-up turn that gates the SAME tool again — the
    /// approveAlways probe: with the standing rule recorded the call must
    /// auto-pass and the script advance to <see cref="DoneText"/>.</summary>
    public static ChatResponseUpdate[] RepeatDeleteTurn(string ticketId) =>
    [
        new(ChatRole.Assistant, "Deleted it.\n"),
        new(ChatRole.Assistant, [new FunctionCallContent("call-2", "delete_ticket",
            new Dictionary<string, object?> { ["id"] = ticketId })]),
    ];

    /// <summary>A follow-up turn that gates a DIFFERENT destructive tool —
    /// the re-pause probe: the resumed run must surface a second approval
    /// event and end the stream there, not swallow the pause.</summary>
    public static ChatResponseUpdate[] DeleteThenEscalateTurn(string ticketId) =>
    [
        new(ChatRole.Assistant, "Deleted it.\n"),
        new(ChatRole.Assistant, [new FunctionCallContent("call-3", "escalate_ticket",
            new Dictionary<string, object?> { ["id"] = ticketId, ["reason"] = "customer escalating" })]),
    ];

    /// <summary>A plain text-only turn — the multi-turn memory probe's
    /// replies (no tool calls, so the run ends in one model call).</summary>
    public static ChatResponseUpdate[] TextTurn(string text) => [new(ChatRole.Assistant, text)];

    /// <summary>The burst probe (Task 3): ONE turn asking for BOTH
    /// destructive tools, each FunctionCallContent in its own streaming
    /// update — the shape P08 observed glm-5.3 burst in (several gated
    /// calls back to back within one model turn). Both must surface as
    /// approval requests and both must park, each under its own
    /// requestId.</summary>
    public static ChatResponseUpdate[] BurstTurn(string ticketId) =>
    [
        new(ChatRole.Assistant, "I'll delete that ticket and escalate it for visibility.\n"),
        new(ChatRole.Assistant, [new FunctionCallContent("call-1", "delete_ticket",
            new Dictionary<string, object?> { ["id"] = ticketId })]),
        new(ChatRole.Assistant, [new FunctionCallContent("call-2", "escalate_ticket",
            new Dictionary<string, object?> { ["id"] = ticketId, ["reason"] = "burst probe" })]),
    ];

    /// <summary>Post-burst narration, reached only after BOTH parked calls
    /// were answered and executed.</summary>
    public static ChatResponseUpdate[] BurstDoneText =>
    [
        new(ChatRole.Assistant, "Both done.\n"),
    ];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("P13's SSE path is streaming only");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SeenMessages.Add(messages.ToList());
        var script = _index < scripts.Length ? scripts[_index] : scripts[^1];
        _index++;
        foreach (var update in script)
        {
            await Task.Yield();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
