using System.Text.Json;
using MafDemo.Core.Domain;

namespace MafDemo.Core.Stores;

/// <summary>
/// JSON-file-backed <see cref="ITicketStore"/> implementation.
/// Loads existing tickets on construction (missing file = starts empty; corrupt
/// or duplicate-id file = moved to <c>.corrupt</c> and starts empty) and rewrites the whole file
/// after each mutation. Safe for concurrent callers (single gate lock; the
/// whole file rewrites under it — the demo's file IS the database, so writes serialize).
/// </summary>
public class FileTicketStore(string path) : ITicketStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Ticket> _tickets = Load(path);

    private static Dictionary<Guid, Ticket> Load(string p)
    {
        if (!File.Exists(p)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<Ticket>>(File.ReadAllText(p)) ?? [];
            return list.ToDictionary(t => t.Id);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Corrupt or unusable file (a crash mid-write before atomic saves
            // existed, or a duplicate ticket Id from two app instances sharing
            // a work directory): preserve the bad data as <path>.corrupt so the
            // user can inspect it, but start empty rather than throwing from
            // the ctor and bricking every P04+ project that constructs this store.
            File.Move(p, p + ".corrupt", overwrite: true);
            return [];
        }
    }

    private void Save()
    {
        // Atomic write: serialize into a sibling temp file, then Move over the
        // real path. A crash mid-write leaves the previous file intact instead
        // of a truncated tickets.json that would brick the next startup.
        // Called only with _gate held, so concurrent writers never share the tmp path.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_tickets.Values.ToList(), Json));
        File.Move(tmp, path, overwrite: true);
    }

    public Task<Ticket> CreateAsync(string title, string description, TicketPriority priority)
    {
        Ticket t;
        lock (_gate)
        {
            t = new Ticket(Guid.NewGuid(), title, description, priority, TicketStatus.Open,
                null, DateTimeOffset.UtcNow, []);
            _tickets[t.Id] = t;
            Save();
        }
        return Task.FromResult(t);
    }

    public Task<Ticket?> GetAsync(Guid id)
    {
        lock (_gate)
        {
            return Task.FromResult(_tickets.GetValueOrDefault(id));
        }
    }

    public Task<IReadOnlyList<Ticket>> ListAsync()
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Ticket>>([.. _tickets.Values]);
        }
    }

    public Task<Ticket?> UpdateStatusAsync(Guid id, TicketStatus status)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(id, out var t)) return Task.FromResult<Ticket?>(null);
            t = t with { Status = status };
            _tickets[id] = t;
            Save();
            return Task.FromResult<Ticket?>(t);
        }
    }

    public Task<bool> AddNoteAsync(Guid id, string note)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(id, out var t)) return Task.FromResult(false);
            _tickets[id] = t with { Notes = [.. t.Notes, note] };
            Save();
            return Task.FromResult(true);
        }
    }
}
