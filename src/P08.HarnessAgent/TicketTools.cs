using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using Microsoft.Extensions.AI;

namespace P08.HarnessAgent;

/// <summary>
/// Ticket-store tool bodies for the P08 harness batch. Wrapper-function style
/// per P02's <c>TicketToolFunctions</c>, but P08-local: the overnight agent
/// needs <c>get_ticket</c> and <c>close_ticket</c> (P02 has neither), and the
/// wire names are pinned explicitly in <see cref="All"/> following P06's
/// snake_case convention. Bad ids and misses return in-band messages instead
/// of throwing, so the model can recover mid-batch without killing the run.
/// </summary>
public sealed class TicketTools(ITicketStore store)
{
    /// <summary>Creates a ticket; unparsable priorities fall back to Normal
    /// (same tolerance as P02).</summary>
    public async Task<string> CreateTicketAsync(string title, string description, string priority)
    {
        var p = Enum.TryParse<TicketPriority>(priority, ignoreCase: true, out var parsed)
            ? parsed
            : TicketPriority.Normal;
        var t = await store.CreateAsync(title, description, p);
        return $"Created ticket {t.Id} (priority {t.Priority})";
    }

    /// <summary>Lists every ticket, newest context first for the model:
    /// one line per ticket with id, status, priority and title.</summary>
    public async Task<string> ListTicketsAsync()
    {
        var tickets = await store.ListAsync();
        return tickets.Count == 0 ? "(no tickets)"
            : string.Join("\n", tickets.Select(t => $"{t.Id} | {t.Status} | {t.Priority} | {t.Title}"));
    }

    /// <summary>Looks a ticket up by GUID and renders its full record,
    /// including accumulated notes — the shape the agent checks before
    /// resolving a ticket.</summary>
    public async Task<string> GetTicketAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return $"Invalid ticket id: {id}";
        var ticket = await store.GetAsync(guid);
        return ticket is null
            ? $"Ticket {id} not found"
            : $"{ticket.Id} | {ticket.Status} | {ticket.Priority} | {ticket.Title}\n{ticket.Description}"
              + (ticket.Notes.Count == 0 ? "" : $"\nnotes: {string.Join(" / ", ticket.Notes)}");
    }

    /// <summary>Appends a resolution note to a ticket — the agent's main
    /// write action in the batch loop.</summary>
    public async Task<string> AddTicketNoteAsync(string id, string note)
    {
        if (!Guid.TryParse(id, out var guid)) return "Invalid ticket id.";
        if (await store.GetAsync(guid) is null) return $"Ticket {id} not found";
        await store.AddNoteAsync(guid, note);
        return $"Note added to {id}.";
    }

    /// <summary>Closes a ticket. Only Closed is offered (the batch's terminal
    /// state) — status transitions beyond that stay out of the agent's hands.</summary>
    public async Task<string> CloseTicketAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return "Invalid ticket id.";
        var closed = await store.UpdateStatusAsync(guid, TicketStatus.Closed);
        return closed is null ? $"Ticket {id} not found" : $"Ticket {id} now {closed.Status}";
    }

    /// <summary>
    /// The full tool set for the harness agent, wire names pinned explicitly
    /// (P06 convention) so the model-facing contract is stable regardless of
    /// how <see cref="AIFunctionFactory"/> would derive names from the
    /// <c>*Async</c> methods. <c>close_ticket</c> — the batch's one
    /// irreversible action — is wrapped in
    /// <see cref="ApprovalRequiredAIFunction"/> so the harness approval flow
    /// holds it for a human decision instead of running it unattended
    /// (<see cref="ApprovalPolicy"/> auto-approves only the read-only set).
    /// </summary>
    public static AIFunction[] All(TicketTools tools) =>
    [
        AIFunctionFactory.Create(tools.CreateTicketAsync,
            name: "create_ticket",
            description: "Create a new support ticket with a title, description and priority (Low, Normal, High or Critical)."),
        AIFunctionFactory.Create(tools.ListTicketsAsync,
            name: "list_tickets",
            description: "List all support tickets, one per line: id, status, priority and title."),
        AIFunctionFactory.Create(tools.GetTicketAsync,
            name: "get_ticket",
            description: "Look up a support ticket by its GUID and return its status, priority, title, description and notes."),
        AIFunctionFactory.Create(tools.AddTicketNoteAsync,
            name: "add_note",
            description: "Append a note (e.g. a resolution summary) to a support ticket identified by its GUID."),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(tools.CloseTicketAsync,
            name: "close_ticket",
            description: "Close a support ticket identified by its GUID after its resolution is recorded.")),
    ];
}
