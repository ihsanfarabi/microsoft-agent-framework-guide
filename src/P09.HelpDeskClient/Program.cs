using A2A;
using MafDemo.AgentCommon;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.AI;

// OTLP tracing so the Aspire dashboard shows the call flowing into the
// inventory service's process (spec requirement 6).
using var telemetry = Telemetry.StartOtlp("P09.HelpDeskClient");

// Discover the remote agent: the resolver takes the remote HOST base URI and
// fetches its card from /.well-known/agent-card.json (verified in the A2A
// package — the resolver default path matches what MapWellKnownAgentCard serves).
var resolver = new A2ACardResolver(new Uri("http://localhost:5199"));
AIAgent remote = await resolver.GetAIAgentAsync();
Console.WriteLine($"[discovered] {remote.Name} via well-known agent card");

// Expose the remote agent to the local helpdesk agent as a single function
// tool — one message to InventoryAgent can span many inventory tool calls.
var client = new ChatClientBuilder(OllamaChat.Create())
    .UseFunctionInvocation()
    .Build();
var helpdesk = new ChatClientAgent(client, name: "HelpDeskAgent",
    instructions: """
        You are a helpdesk agent handling IT tickets. When a ticket needs a
        loaner laptop, delegate stock checks and reservations to the
        inventory agent tool and report its answer.
        """,
    tools: [remote.AsAIFunction()]);

Console.WriteLine("Ticket 4 needs a loaner laptop. Check stock and reserve one if possible.");
AgentResponse reply = await helpdesk.RunAsync(
    "Ticket 4 needs a loaner laptop. Check stock and reserve one if possible.");
Console.WriteLine();
Console.WriteLine($"[helpdesk] {reply.Text}");