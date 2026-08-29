using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;

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

// MCP scenario: start the filesystem MCP server over stdio (npx downloads it
// on first use), list the tools it exposes, and merge them into a second
// agent instance alongside the built-in ticket function tools. The client is
// disposed at the end of this scope, which shuts down the server process.
string sandboxPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sandbox"));

await using var mcpClient = await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Name = "SandboxFileSystem",
    Command = "npx",
    Arguments = ["-y", "@modelcontextprotocol/server-filesystem", sandboxPath],
}));
IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync();
Console.WriteLine($"--- MCP server tools: {string.Join(", ", mcpTools.Select(t => t.Name))} ---");
Console.WriteLine();

var mcpAgent = TicketBot.CreateWithMcp(store, mcpTools.Cast<AITool>());
Console.WriteLine(await mcpAgent.RunAsync("What files are in the sandbox and what does the readme say?"));
Console.WriteLine();

Console.WriteLine("--- final store state ---");
foreach (var ticket in await store.ListAsync())
    Console.WriteLine(
        $"{ticket.Id} | {ticket.Status} | {ticket.Priority} | {ticket.Title}" +
        (ticket.Notes.Count == 0 ? "" : $" | notes: {string.Join(" / ", ticket.Notes)}"));
