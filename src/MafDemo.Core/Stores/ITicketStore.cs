using MafDemo.Core.Domain;

namespace MafDemo.Core.Stores;

public interface ITicketStore
{
    Task<Ticket> CreateAsync(string title, string description, TicketPriority priority);
    Task<Ticket?> GetAsync(Guid id);
    Task<IReadOnlyList<Ticket>> ListAsync();
    Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status);
    /// <summary>Returns false if the ticket id is unknown (no throw).</summary>
    Task<bool> AddNoteAsync(Guid id, string note);
}
