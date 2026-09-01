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

        using var first = new StringContent("""{"text":"delete the broken ticket"}""", Encoding.UTF8, "application/json");
        using var firstResponse = await client.PostAsync("/conversations/c2/messages", first);
        Assert.Contains("event: approval", await firstResponse.Content.ReadAsStringAsync());

        using var second = new StringContent("""{"text":"just list them"}""", Encoding.UTF8, "application/json");
        using var secondResponse = await client.PostAsync("/conversations/c2/messages", second);
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

        using var content = new StringContent("""{"text":"delete that ticket"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/conversations/c1/messages", content);

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
        using var first = await client.PostAsync("/conversations/cA/messages", message);
        var firstBody = await first.Content.ReadAsStringAsync();
        var requestId = ExtractRequestId(firstBody);
        Assert.Contains(ticket.Id, (await store.ListAsync()).Select(t => t.Id)); // nothing deleted while paused

        using var vote = new StringContent(
            $$"""{"requestId":"{{requestId}}","approved":true,"reason":"operator approved"}""", Encoding.UTF8,
            "application/json");
        using var resume = await client.PostAsync("/approvals/cA", vote);

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
        using var first = await client.PostAsync("/conversations/cD/messages", message);
        var requestId = ExtractRequestId(await first.Content.ReadAsStringAsync());

        using var vote = new StringContent(
            $$"""{"requestId":"{{requestId}}","approved":false,"reason":"operator declined"}""", Encoding.UTF8,
            "application/json");
        using var resume = await client.PostAsync("/approvals/cD", vote);
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
        using var first = await client.PostAsync("/conversations/cE/messages", message);
        var requestId = ExtractRequestId(await first.Content.ReadAsStringAsync());

        using var vote = new StringContent($$"""{"requestId":"{{requestId}}","approveAlways":true}""",
            Encoding.UTF8, "application/json");
        using var resume = await client.PostAsync("/approvals/cE", vote);
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
        using var first = await client.PostAsync("/conversations/cF/messages", message);
        var requestId = ExtractRequestId(await first.Content.ReadAsStringAsync());

        using var vote = new StringContent($$"""{"requestId":"{{requestId}}","approved":true}""", Encoding.UTF8,
            "application/json");
        using var resume = await client.PostAsync("/approvals/cF", vote);
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
    public async Task Approvals_endpoint_returns_404_for_unknown_request_id()
    {
        using var factory = FactoryWith(
            new ScriptedClient(ScriptedClient.DeleteTicketTurn("call-1", "00000000-0000-0000-0000-000000000009")));
        var client = factory.CreateClient();

        using var vote = new StringContent("""{"requestId":"no-such-request","approved":true}""", Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/approvals/cZ", vote);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>A factory whose chat client is the scripted fake and whose
    /// ticket store is the caller's own file-backed one — so the gated call's
    /// ticket id can be a real, pre-created ticket and the store's tombstone
    /// state is directly assertable.</summary>
    private static WebApplicationFactory<Program> FactoryWith(ScriptedClient client,
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
        var payload = body.Split("event: approval\ndata: ", 2)[1].Split("\n\n", 2)[0];
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("requestId").GetString()
               ?? throw new InvalidOperationException("approval frame carried no requestId");
    }

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

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("P13's SSE path is streaming only");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
