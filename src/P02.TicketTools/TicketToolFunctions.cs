using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

namespace P02.TicketTools;

public class TicketToolFunctions(ITicketStore store)
{
    public async Task<string> CreateTicketAsync(string title, string description, string priority)
    {
        var p = Enum.TryParse<TicketPriority>(priority, ignoreCase: true, out var parsed) ? parsed : TicketPriority.Normal;
        var t = await store.CreateAsync(title, description, p);
        return $"Created ticket {t.Id} (priority {t.Priority})";
    }

    public async Task<string> ListTicketsAsync()
    {
        var tickets = await store.ListAsync();
        return tickets.Count == 0 ? "(none)"
            : string.Join("\n", tickets.Select(t => $"{t.Id} | {t.Status} | {t.Priority} | {t.Title}"));
    }

    public async Task<string> UpdateTicketStatusAsync(string id, string status)
    {
        if (!Guid.TryParse(id, out var guid) || !Enum.TryParse<TicketStatus>(status, ignoreCase: true, out var st))
            return "Invalid id or status.";
        var updated = await store.UpdateStatusAsync(guid, st);
        return updated is null ? $"Ticket {id} not found" : $"Ticket {id} now {updated.Status}";
    }

    public async Task<string> AddTicketNoteAsync(string id, string note)
    {
        if (!Guid.TryParse(id, out var guid)) return "Invalid id.";
        var ticket = await store.GetAsync(guid);
        if (ticket is null) return $"Ticket {id} not found";
        await store.AddNoteAsync(guid, note);
        return $"Note added to {id}.";
    }
}
