using System.Text.Json;
using MafDemo.Core.Domain;

namespace MafDemo.Core.Stores;

/// <summary>
/// JSON-file-backed <see cref="ITicketStore"/> implementation.
/// Loads existing tickets on construction (missing file = starts empty) and
/// rewrites the whole file after each mutation. Single-threaded demo store — no locking.
/// </summary>
public class FileTicketStore(string path) : ITicketStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Dictionary<Guid, Ticket> _tickets = Load(path);

    private static Dictionary<Guid, Ticket> Load(string p)
    {
        if (!File.Exists(p)) return [];
        var list = JsonSerializer.Deserialize<List<Ticket>>(File.ReadAllText(p)) ?? [];
        return list.ToDictionary(t => t.Id);
    }

    private void Save() =>
        File.WriteAllText(path, JsonSerializer.Serialize(_tickets.Values.ToList(), Json));

    public Task<Ticket> CreateAsync(string title, string description, TicketPriority priority)
    {
        var t = new Ticket(Guid.NewGuid(), title, description, priority, TicketStatus.Open,
            null, DateTimeOffset.UtcNow, []);
        _tickets[t.Id] = t;
        Save();
        return Task.FromResult(t);
    }

    public Task<Ticket?> GetAsync(Guid id) =>
        Task.FromResult(_tickets.GetValueOrDefault(id));

    public Task<IReadOnlyList<Ticket>> ListAsync() =>
        Task.FromResult<IReadOnlyList<Ticket>>([.. _tickets.Values]);

    public Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status)
    {
        if (!_tickets.TryGetValue(id, out var t)) return Task.FromResult<Ticket?>(null);
        t = t with { Status = status };
        _tickets[id] = t;
        Save();
        return Task.FromResult<Ticket?>(t);
    }

    public Task<bool> AddNoteAsync(Guid id, string note)
    {
        if (!_tickets.TryGetValue(id, out var t)) return Task.FromResult(false);
        _tickets[id] = t with { Notes = [.. t.Notes, note] };
        Save();
        return Task.FromResult(true);
    }
}
