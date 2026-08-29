using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

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
}