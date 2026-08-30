using MafDemo.Core.Stores;
using P08.HarnessAgent;

namespace P08.HarnessAgent.Tests;

public class BacklogSeedTests
{
    [Fact]
    public async Task Seed_creates_five_tickets_and_is_idempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p08-{Guid.NewGuid():N}");
        var store = new FileTicketStore(dir);
        await BacklogSeed.RunAsync(store);
        await BacklogSeed.RunAsync(store); // second run must not duplicate
        Assert.Equal(5, (await store.ListAsync()).Count);
    }
}