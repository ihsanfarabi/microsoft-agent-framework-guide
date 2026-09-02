using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P13.StreamingApproval.Agents;

/// <summary>
/// Constructs the P13 streaming-approval agent: the shared <c>OllamaChat</c>
/// client (or a scripted fake, in tests) wrapped in a P02-style
/// <see cref="ChatClientAgent"/> — <c>UseFunctionInvocation</c> runs the
/// client-side tool loop — with MAF 1.19.0's approval middleware
/// (<c>UseToolApproval</c>) layered on top via <see cref="AIAgentBuilder"/>,
/// so sensitive tool calls surface <see cref="ToolApprovalRequestContent"/> in
/// the update stream instead of executing.
/// </summary>
public static class TicketAgent
{
    /// <summary>Builds the approval-aware ticket agent over the given client
    /// and store. Read-only tools pass the auto-approval rule; the destructive
    /// ones are wrapped in <see cref="ApprovalRequiredAIFunction"/> and hold
    /// for an explicit approval (the resume endpoint, a later task).</summary>
    public static AIAgent Build(IChatClient client, IDeletableTicketStore store)
    {
        // UseFunctionInvocation is required (P02 TicketBot pattern): it runs
        // the client-side tool loop that a plain IChatClient does not, and it
        // is the layer that converts a call to an ApprovalRequiredAIFunction
        // into a ToolApprovalRequestContent instead of an execution.
        IChatClient chatClient = new ChatClientBuilder(client)
            .UseFunctionInvocation()
            .Build();

        ChatClientAgent inner = new(chatClient, new ChatClientAgentOptions
        {
            Name = "TicketAgent",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    """
                    You are HelpDeskHQ's ticket agent. List and look up tickets with the
                    read-only tools, and mutate a ticket only when the user asks: delete
                    a ticket with delete_ticket, or escalate one with escalate_ticket.
                    Never invent ticket IDs — always use an id returned by a tool.
                    """,
                Tools = TicketToolSet.All(new TicketToolSet(store)),
            },
        });

        // The approval middleware adds the "don't ask again" auto-approval
        // pass over the requests the function-invocation layer surfaces: its
        // rule list decides which ToolApprovalRequestContents bypass the human.
        // A false return is not a rejection — it just means "human decides".
        return new AIAgentBuilder(inner)
            .UseToolApproval(new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [TicketApprovalPolicy.ShouldAutoApprove],
            })
            .Build();
    }
}

/// <summary>
/// Ticket-store tool bodies for the P13 streaming agent, wrapper-function
/// style per P08's <c>TicketTools</c> with wire names pinned explicitly
/// (P06 convention). The destructive pair — <c>delete_ticket</c> and
/// <c>escalate_ticket</c> — is wrapped in <see cref="ApprovalRequiredAIFunction"/>,
/// so a model call to either surfaces a <see cref="ToolApprovalRequestContent"/>
/// (surfaced to the SSE client by Program.cs) rather than mutating the store.
/// </summary>
public sealed class TicketToolSet(IDeletableTicketStore store)
{
    /// <summary>Lists every ticket, one line per ticket with id, status,
    /// priority and title — the read the agent makes before proposing a
    /// destructive change.</summary>
    public async Task<string> ListTicketsAsync()
    {
        var tickets = await store.ListAsync();
        return tickets.Count == 0 ? "(no tickets)"
            : string.Join("\n", tickets.Select(t => $"{t.Id} | {t.Status} | {t.Priority} | {t.Title}"));
    }

    /// <summary>Looks a ticket up by id and renders its full record,
    /// including accumulated notes.</summary>
    public async Task<string> GetTicketAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return $"Invalid ticket id: {id}";
        var ticket = await store.GetAsync(guid);
        return ticket is null
            ? $"Ticket {id} not found"
            : $"{ticket.Id} | {ticket.Status} | {ticket.Priority} | {ticket.Title}\n{ticket.Description}"
              + (ticket.Notes.Count == 0 ? "" : $"\nnotes: {string.Join(" / ", ticket.Notes)}");
    }

    /// <summary>Deletes a ticket permanently — the flagship destructive action
    /// of the approval round-trip: without an approved resume the record is
    /// never touched.</summary>
    public async Task<string> DeleteTicketAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return "Invalid ticket id.";
        return await store.DeleteAsync(guid)
            ? $"Ticket {id} deleted."
            : $"Ticket {id} not found";
    }

    /// <summary>Escalates a ticket: marks it InProgress and appends an
    /// ESCALATED note carrying the reason. Gated like delete — escalation
    /// pages humans, so the model alone must not be able to fire it.</summary>
    public async Task<string> EscalateTicketAsync(string id, string reason)
    {
        if (!Guid.TryParse(id, out var guid)) return "Invalid ticket id.";
        var ticket = await store.GetAsync(guid);
        if (ticket is null) return $"Ticket {id} not found";
        await store.UpdateStatusAsync(guid, TicketStatus.InProgress);
        await store.AddNoteAsync(guid, $"ESCALATED: {reason}");
        return $"Ticket {id} escalated (now InProgress, escalation note added).";
    }

    /// <summary>The full tool set, wire names pinned explicitly. The two
    /// destructive tools ride inside <see cref="ApprovalRequiredAIFunction"/>
    /// so the harness holds them for a human decision; the read-only pair is
    /// auto-approved by <see cref="TicketApprovalPolicy"/>.</summary>
    public static AIFunction[] All(TicketToolSet tools) =>
    [
        AIFunctionFactory.Create(tools.ListTicketsAsync,
            name: "list_tickets",
            description: "List all support tickets, one per line: id, status, priority and title."),
        AIFunctionFactory.Create(tools.GetTicketAsync,
            name: "get_ticket",
            description: "Look up a support ticket by its GUID and return its status, priority, title, description and notes."),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(tools.DeleteTicketAsync,
            name: "delete_ticket",
            description: "Permanently delete a support ticket identified by its GUID. Requires approval.")),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(tools.EscalateTicketAsync,
            name: "escalate_ticket",
            description: "Escalate a support ticket identified by its GUID: mark it InProgress and record an escalation note. Requires approval.")),
    ];
}

/// <summary>
/// Auto-approval boundary for the P13 agent: the read-only tools never need a
/// human, everything else (delete_ticket, escalate_ticket — anything not on
/// the list) falls through to the approval request. Mirrors P08's
/// <see cref="P08.HarnessAgent.ApprovalPolicy"/> shape, P13-local because the
/// tool sets differ.
/// </summary>
public static class TicketApprovalPolicy
{
    /// <summary>Wire names that never require a human.</summary>
    private static readonly HashSet<string> ReadOnly = ["list_tickets", "get_ticket"];

    /// <summary>True when the call targets a read-only tool and may run
    /// without approval; false for anything else, which then needs an
    /// explicit approval response.</summary>
    public static ValueTask<bool> ShouldAutoApprove(ToolAutoApprovalRuleContext context) =>
        ValueTask.FromResult(ReadOnly.Contains(context.FunctionCallContent.Name));
}

/// <summary>
/// The P13 store contract: <see cref="ITicketStore"/> plus the delete verb.
/// P13-local because <c>ITicketStore</c> itself has no delete verb and Core
/// is frozen for this project.
/// </summary>
public interface IDeletableTicketStore : ITicketStore
{
    /// <summary>Deletes a ticket permanently; false when it does not exist
    /// (or was already deleted).</summary>
    Task<bool> DeleteAsync(Guid id);
}

/// <summary>
/// File-backed ticket store with delete, layered over the shared
/// <see cref="FileTicketStore"/> because <c>ITicketStore</c> itself has no
/// delete verb and Core is frozen for P13. Deleted ids are recorded in their
/// own file and hidden from <see cref="GetAsync"/>/<see cref="ListAsync"/>;
/// the inner store's file and cache are never touched behind its back, so a
/// later note/status write through the inner store still sees a consistent
/// (filtered) view. Same corrupt-file tolerance as the inner store. Safe for
/// concurrent callers: the inner store locks itself; the tombstone set is
/// guarded by this class's own gate.
/// </summary>
public sealed class DeletableTicketStore : IDeletableTicketStore
{
    private readonly FileTicketStore _inner;
    private readonly string _deletedPath;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _deleted;

    public DeletableTicketStore(string ticketsPath, string deletedPath)
    {
        _inner = new FileTicketStore(ticketsPath);
        _deletedPath = deletedPath;
        _deleted = LoadDeleted(deletedPath);
    }

    private HashSet<Guid> LoadDeleted(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return [.. System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(File.ReadAllText(path)) ?? []];
        }
        catch (System.Text.Json.JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return [];
        }
    }

    private void SaveDeleted()
    {
        var tmp = _deletedPath + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(_deleted.ToList()));
        File.Move(tmp, _deletedPath, overwrite: true);
    }

    /// <summary>Deletes the ticket: recorded in the deleted set (the delete of
    /// record — the inner file is left alone so a torn tombstone write can
    /// never lose ticket data) and hidden from reads. Idempotent.</summary>
    public async Task<bool> DeleteAsync(Guid id)
    {
        if (await _inner.GetAsync(id) is null) return false;
        lock (_gate)
        {
            if (_deleted.Contains(id)) return false;
            _deleted.Add(id);
            SaveDeleted();
        }
        return true;
    }

    public async Task<Ticket?> GetAsync(Guid id)
    {
        if (await _inner.GetAsync(id) is null) return null;
        lock (_gate)
        {
            if (_deleted.Contains(id)) return null;
        }
        return await _inner.GetAsync(id);
    }

    public async Task<IReadOnlyList<Ticket>> ListAsync()
    {
        Guid[] deletedSnapshot;
        lock (_gate)
        {
            deletedSnapshot = [.. _deleted];
        }
        var deletedSet = deletedSnapshot.ToHashSet();
        return (await _inner.ListAsync()).Where(t => !deletedSet.Contains(t.Id)).ToList();
    }

    public Task<Ticket> CreateAsync(string title, string description, TicketPriority priority) =>
        _inner.CreateAsync(title, description, priority);

    public Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status) =>
        _inner.UpdateStatusAsync(id, status);

    public Task<bool> AddNoteAsync(Guid id, string note) =>
        _inner.AddNoteAsync(id, note);
}
