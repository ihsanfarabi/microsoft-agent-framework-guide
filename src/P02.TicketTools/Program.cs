using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

// Start OTel tracing first so the provider is listening before any model call.
// Disposed on exit, which flushes the spans to the console exporter.
using var telemetry = Telemetry.Start("P02.TicketTools");

var store = new InMemoryTicketStore();
var agent = TicketBot.Create(store);

Console.WriteLine(await agent.RunAsync("File a ticket for my broken VPN, priority high."));
Console.WriteLine();
Console.WriteLine(await agent.RunAsync("List my tickets."));
Console.WriteLine();
Console.WriteLine(await agent.RunAsync("Mark the VPN ticket resolved."));
Console.WriteLine();

Console.WriteLine("--- final store state ---");
foreach (var ticket in await store.ListAsync())
    Console.WriteLine(
        $"{ticket.Id} | {ticket.Status} | {ticket.Priority} | {ticket.Title}" +
        (ticket.Notes.Count == 0 ? "" : $" | notes: {string.Join(" / ", ticket.Notes)}"));
