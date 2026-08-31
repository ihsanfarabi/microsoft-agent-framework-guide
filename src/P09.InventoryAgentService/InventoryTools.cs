using AIFunctionFactory = Microsoft.Extensions.AI.AIFunctionFactory;
using Microsoft.Extensions.AI;
using MafDemo.Core.Inventory;

namespace P09.InventoryAgentService;

/// <summary>
/// The inventory agent's tool surface, exposed to remote A2A callers
/// (spec requirement 1: check_stock + reserve_laptop only). Tools are
/// async delegates straight onto <see cref="IInventoryStore"/>.
/// </summary>
public static class InventoryTools
{
    public static AITool[] All(IInventoryStore store) =>
    [
        AIFunctionFactory.Create(
            (string sku) => store.GetAsync(sku),
            name: "check_stock",
            description: "Check loaner laptop stock by SKU. Returns the item (model, available, reserved) or null when the SKU is unknown."),
        AIFunctionFactory.Create(
            (string sku) => store.TryReserveAsync(sku),
            name: "reserve_laptop",
            description: "Reserve one loaner laptop by SKU. Returns false when out of stock or the SKU is unknown."),
    ];
}