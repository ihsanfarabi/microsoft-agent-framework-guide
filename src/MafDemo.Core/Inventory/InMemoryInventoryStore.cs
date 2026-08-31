using System.Collections.Concurrent;

namespace MafDemo.Core.Inventory;

/// <summary>
/// Deterministic fake backing the P09 inventory agent (spec requirement 1).
/// Thread-safe so concurrent A2A requests can't double-reserve the last unit:
/// the reservation check-and-set runs inside the dictionary's lock.
/// </summary>
public sealed class InMemoryInventoryStore : IInventoryStore
{
    private readonly ConcurrentDictionary<string, InventoryItem> _items = new();

    /// <summary>Test/service seeding: replaces the stock list wholesale.</summary>
    public void Seed(IReadOnlyList<InventoryItem> items)
    {
        _items.Clear();
        foreach (var item in items)
            _items[item.Sku] = item;
    }

    public Task<IReadOnlyList<InventoryItem>> ListAsync() =>
        Task.FromResult<IReadOnlyList<InventoryItem>>([.. _items.Values.OrderBy(i => i.Sku, StringComparer.Ordinal)]);

    public Task<InventoryItem?> GetAsync(string sku) =>
        Task.FromResult(_items.TryGetValue(sku, out var item) ? item : null);

    public Task<bool> TryReserveAsync(string sku)
    {
        while (true)
        {
            if (!_items.TryGetValue(sku, out var item))
                return Task.FromResult(false);
            if (item.Available == 0)
                return Task.FromResult(false);

            var reserved = item with { Available = item.Available - 1, Reserved = item.Reserved + 1 };
            if (_items.TryUpdate(sku, reserved, item))
                return Task.FromResult(true);
            // lost the race against a concurrent reserve — retry
        }
    }
}