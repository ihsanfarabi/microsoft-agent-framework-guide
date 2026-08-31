namespace MafDemo.Core.Inventory;

/// <summary>
/// Read + reserve surface the inventory agent's tools drive. Reservation is
/// single-shot (<c>TryReserveAsync</c>) — the A2A agent never mutates stock
/// any other way.
/// </summary>
public interface IInventoryStore
{
    Task<IReadOnlyList<InventoryItem>> ListAsync();
    Task<InventoryItem?> GetAsync(string sku);
    Task<bool> TryReserveAsync(string sku);
}