using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using P08.HarnessAgent;

namespace P08.HarnessAgent.Tests;

public class BacklogSeedTests
{
    [Fact]
    public async Task Seed_creates_five_tickets_and_is_idempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"p08-{Guid.NewGuid():N}");
        var store = new FileTicketStore(path);
        await BacklogSeed.RunAsync(store);
        await BacklogSeed.RunAsync(store); // second run must not duplicate
        Assert.Equal(5, (await store.ListAsync()).Count);
        File.Delete(path);
    }

    [Fact]
    public async Task Seed_resumes_a_partial_seed_without_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"p08-{Guid.NewGuid():N}");
        var store = new FileTicketStore(path);
        // Simulate a crash after 2 of the 5 creates: two backlog titles are
        // already in the store. Re-running must seed only the three missing
        // titles — not all five on top of the two — landing at exactly 5.
        await store.CreateAsync("VPN fails from home network", "Cannot connect since router change", TicketPriority.High);
        await store.CreateAsync("Printer offline in pod 4", "Queue stuck", TicketPriority.Low);

        await BacklogSeed.RunAsync(store);

        var tickets = await store.ListAsync();
        Assert.Equal(5, tickets.Count);
        Assert.Equal(5, tickets.Select(t => t.Title).Distinct().Count());
        File.Delete(path);
    }
}
