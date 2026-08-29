using MafDemo.Core.Domain;

namespace MafDemo.Core.Stores;

/// <summary>
/// Dictionary-backed, in-memory <see cref="ITicketStore"/> implementation.
/// Single-threaded demo store — no locking. Updates use non-destructive record copies.
/// </summary>
public class InMemoryTicketStore : ITicketStore
{
    private readonly Dictionary<Guid, Ticket> _tickets = [];

    public Task<Ticket> CreateAsync(string title, string description, TicketPriority priority)
    {
        var ticket = new Ticket(
            Id: Guid.NewGuid(),
            Title: title,
            Description: description,
            Priority: priority,
            Status: TicketStatus.Open,
            Assignee: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Notes: []);
        _tickets[ticket.Id] = ticket;
        return Task.FromResult(ticket);
    }

    public Task<Ticket?> GetAsync(Guid id)
    {
        _tickets.TryGetValue(id, out var ticket);
        return Task.FromResult(ticket);
    }

    public Task<IReadOnlyList<Ticket>> ListAsync() =>
        Task.FromResult<IReadOnlyList<Ticket>>(_tickets.Values.ToList());

    public Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status)
    {
        if (!_tickets.TryGetValue(id, out var ticket))
        {
            return Task.FromResult<Ticket?>(null);
        }

        var updated = ticket with { Status = status };
        _tickets[id] = updated;
        return Task.FromResult<Ticket?>(updated);
    }

    public Task AddNoteAsync(Guid id, string note)
    {
        var ticket = _tickets[id];
        _tickets[id] = ticket with { Notes = [.. ticket.Notes, note] };
        return Task.CompletedTask;
    }
}