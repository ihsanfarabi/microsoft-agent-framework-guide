using A2A;
using A2A.AspNetCore;
using MafDemo.AgentCommon;
using MafDemo.Core.Inventory;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using P09.InventoryAgentService;

// OTLP tracing to the Aspire dashboard (same wiring as P05-P07). The A2A
// request from P09.HelpDeskClient must show up as a cross-process span.
using var telemetry = Telemetry.StartOtlp("P09.InventoryAgentService");

var builder = WebApplication.CreateBuilder(args);

// Fixed port so the agent card URLs and the client's resolver URI are stable.
builder.WebHost.UseUrls("http://localhost:5199");

var store = new InMemoryInventoryStore();
store.Seed([new("LT-001", "ThinkPad T14", 3, 0), new("LT-002", "MacBook Air", 1, 0)]);
builder.Services.AddSingleton<IInventoryStore>(store);

var client = new ChatClientBuilder(OllamaChat.Create())
    .UseFunctionInvocation()
    .Build();
var inventory = new ChatClientAgent(client, name: "InventoryAgent",
    instructions: """
        You are the inventory service of a loaner laptop program. Answer stock
        questions and make reservations using your tools only. When no item is
        available, say so instead of reserving anything.
        """,
    tools: InventoryTools.All(store));

// A2A server plumbing resolves the agent as a keyed AIAgent service by name.
builder.Services.AddKeyedSingleton<AIAgent>("inventory", (_, _) => inventory);
builder.AddA2AServer("inventory");

var app = builder.Build();
app.MapA2AHttpJson("inventory", "/a2a/inventory"); // HTTP+JSON binding
app.MapWellKnownAgentCard(new AgentCard
{
    Name = "InventoryAgent",
    Description = "Loaner laptop stock and reservations.",
    Version = "1.0",
    SupportedInterfaces = [new AgentInterface
    {
        Url = "http://localhost:5199/a2a/inventory",
        ProtocolBinding = ProtocolBindingNames.HttpJson,
        ProtocolVersion = "1.0",
    }],
});
app.Run();