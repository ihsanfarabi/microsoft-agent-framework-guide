using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using P02.TicketTools;

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

// Second MCP client: our own P12 knowledge server over stdio. Its project
// path is resolved the same walk-up way as sandboxPath above; `dotnet run`
// rebuilds it if stale and hosts it as a child process (msbuild chatter goes
// to stderr, so the JSON-RPC stream on stdout stays clean). Disposed at the
// end of this scope, which shuts the child down.
string knowledgeProjectPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "P12.McpKnowledgeServer"));

await using var knowledgeClient = await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Name = "MafCorpKnowledge",
    Command = "dotnet",
    Arguments = ["run", "--project", knowledgeProjectPath],
}));
IList<McpClientTool> knowledgeMcpTools = await knowledgeClient.ListToolsAsync();
Console.WriteLine($"--- knowledge MCP server tools: {string.Join(", ", knowledgeMcpTools.Select(t => t.Name))} ---");
Console.WriteLine();

// One agent, both tool sets: filesystem tools + knowledge tools ride
// alongside the built-in ticket function tools.
var mergedMcpTools = mcpTools.Concat(knowledgeMcpTools).ToList();
Console.WriteLine($"--- merged MCP tools ({mergedMcpTools.Count}): {string.Join(", ", mergedMcpTools.Select(t => t.Name))} ---");

bool hasFileSystemTools = mergedMcpTools.Any(t => t.Name is "list_directory" or "read_file");
bool hasKnowledgeTool = mergedMcpTools.Any(t => t.Name == "search_knowledge");
Console.WriteLine($"--- merged-set check: filesystem={hasFileSystemTools}, search_knowledge={hasKnowledgeTool} ---");
Console.WriteLine();
if (!hasFileSystemTools || !hasKnowledgeTool)
    throw new InvalidOperationException(
        $"merged MCP tool set is incomplete (filesystem={hasFileSystemTools}, search_knowledge={hasKnowledgeTool})");

var mcpAgent = TicketBot.CreateWithMcp(store, mergedMcpTools.Cast<AITool>());
Console.WriteLine(await mcpAgent.RunAsync("What files are in the sandbox and what does the readme say?"));
Console.WriteLine();
Console.WriteLine(await mcpAgent.RunAsync("What's the policy if my password expires?"));
Console.WriteLine();

Console.WriteLine("--- final store state ---");
foreach (var ticket in await store.ListAsync())
    Console.WriteLine(
        $"{ticket.Id} | {ticket.Status} | {ticket.Priority} | {ticket.Title}" +
        (ticket.Notes.Count == 0 ? "" : $" | notes: {string.Join(" / ", ticket.Notes)}"));
