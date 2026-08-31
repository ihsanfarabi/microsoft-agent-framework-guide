using MafDemo.Core.Inventory;
using Xunit;

namespace MafDemo.Core.Tests;

public class InventoryStoreTests
{
    [Fact]
    public async Task Reserve_decrements_available_increments_reserved()
    {
        var store = new InMemoryInventoryStore();
        store.Seed([new InventoryItem("LT-001", "ThinkPad T14", 3, 0)]);
        var ok = await store.TryReserveAsync("LT-001");
        var item = await store.GetAsync("LT-001");
        Assert.True(ok);
        Assert.Equal((2, 1), (item!.Available, item.Reserved));
    }

    [Fact]
    public async Task Reserve_out_of_stock_fails()
    {
        var store = new InMemoryInventoryStore();
        store.Seed([new InventoryItem("LT-002", "MacBook Air", 0, 0)]);
        Assert.False(await store.TryReserveAsync("LT-002"));
    }
}