using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

public class InMemoryTicketStoreTests
{
    [Fact]
    public async Task Create_assigns_id_and_open_status()
    {
        var store = new InMemoryTicketStore();
        var ticket = await store.CreateAsync("VPN broken", "Cannot connect", TicketPriority.High);
        Assert.NotEqual(Guid.Empty, ticket.Id);
        Assert.Equal(TicketStatus.Open, ticket.Status);
    }

    [Fact]
    public async Task AddNote_appends_and_roundtrips()
    {
        var store = new InMemoryTicketStore();
        var t = await store.CreateAsync("t", "d", TicketPriority.Normal);
        await store.AddNoteAsync(t.Id, "tried restart");
        var loaded = await store.GetAsync(t.Id);
        Assert.Contains("tried restart", loaded!.Notes);
    }

    [Fact]
    public async Task UpdateStatus_persists()
    {
        var store = new InMemoryTicketStore();
        var t = await store.CreateAsync("t", "d", TicketPriority.Normal);
        var updated = await store.UpdateStatusAsync(t.Id, TicketStatus.InProgress);
        Assert.Equal(TicketStatus.InProgress, updated!.Status);
    }
}