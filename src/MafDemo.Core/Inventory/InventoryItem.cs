namespace MafDemo.Core.Inventory;

/// <summary>
/// A loaner laptop the inventory agent tracks. <c>Available</c> decreases
/// and <c>Reserved</c> increases atomically on each successful reservation.
/// </summary>
public record InventoryItem(string Sku, string Model, int Available, int Reserved);