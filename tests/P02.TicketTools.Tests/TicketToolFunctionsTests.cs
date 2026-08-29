using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using P02.TicketTools;

public class TicketToolFunctionsTests
{
    [Fact]
    public async Task CreateTicket_returns_id_and_priority()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        var result = await f.CreateTicketAsync("VPN broken", "cannot connect", "High");
        Assert.Contains("High", result);
        Assert.Contains("ticket", result.ToLower());
    }

    [Fact]
    public async Task ListTickets_empty_store_returns_none()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        Assert.Contains("none", (await f.ListTicketsAsync()).ToLower());
    }

    [Fact]
    public async Task ListTickets_lists_created()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        await f.CreateTicketAsync("VPN", "d", "High");
        var listing = await f.ListTicketsAsync();
        Assert.Contains("VPN", listing);
    }

    [Fact]
    public async Task UpdateTicketStatus_unknown_id_says_not_found()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        var result = await f.UpdateTicketStatusAsync(Guid.NewGuid().ToString(), "Resolved");
        Assert.Contains("not found", result.ToLower());
    }

    [Fact]
    public async Task UpdateTicketStatus_happy_path_persists_status_via_store()
    {
        var store = new InMemoryTicketStore();
        var f = new TicketToolFunctions(store);
        var ticket = await store.CreateAsync("VPN", "d", TicketPriority.High);

        var result = await f.UpdateTicketStatusAsync(ticket.Id.ToString(), "Resolved");

        Assert.Contains("Resolved", result);
        var roundTripped = await store.GetAsync(ticket.Id);
        Assert.NotNull(roundTripped);
        Assert.Equal(TicketStatus.Resolved, roundTripped!.Status);
    }

    [Fact]
    public async Task AddTicketNote_invalid_guid_says_invalid_id()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        var result = await f.AddTicketNoteAsync("not-a-guid", "some note");
        Assert.Contains("Invalid id", result);
    }

    [Fact]
    public async Task AddTicketNote_unknown_id_says_not_found()
    {
        var f = new TicketToolFunctions(new InMemoryTicketStore());
        var result = await f.AddTicketNoteAsync(Guid.NewGuid().ToString(), "some note");
        Assert.Contains("not found", result.ToLower());
    }

    [Fact]
    public async Task AddTicketNote_happy_path_persists_note_via_store()
    {
        var store = new InMemoryTicketStore();
        var f = new TicketToolFunctions(store);
        var ticket = await store.CreateAsync("VPN", "d", TicketPriority.High);

        var result = await f.AddTicketNoteAsync(ticket.Id.ToString(), "user rebooted router");

        Assert.Contains("Note added", result);
        var roundTripped = await store.GetAsync(ticket.Id);
        Assert.NotNull(roundTripped);
        Assert.Contains("user rebooted router", roundTripped!.Notes);
    }
}
