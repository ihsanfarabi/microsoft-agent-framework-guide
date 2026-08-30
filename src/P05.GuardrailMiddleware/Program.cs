using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using P05.GuardrailMiddleware;
using P05.GuardrailMiddleware.Middleware;

// Start OTel tracing first so the provider is listening before any model call.
// StartOtlp ships the spans over OTLP to the Aspire dashboard (start it with
// ./aspire-dashboard.sh, then browse http://localhost:18888 -> Traces) instead
// of printing them to the console. The redaction middleware strips PII before
// the messages reach the agent, so what the spans capture for the model input
// is the redacted text.
using var telemetry = Telemetry.StartOtlp("P05.GuardrailMiddleware");

var store = new InMemoryTicketStore();
var baseAgent = TicketAgent.Create(store);

// Seed a ticket directly in the store so the approval scenario targets a
// known id — the agent must not invent one.
var seeded = await store.CreateAsync("VPN issue", "cannot connect to VPN", TicketPriority.High);
Console.WriteLine($"seeded ticket {seeded.Id}");

// Wrap the base agent in the guardrail middlewares. The two run middlewares
// take the run-delegate shape (only the non-streaming func per middleware —
// the 1.19.0 builder reuses it for RunAsync and RunStreamingAsync); the
// approval middleware takes the function-invocation delegate shape and hooks
// the FunctionInvokingChatClient that P02's TicketBot already puts in the
// chat pipeline, so it sees every tool call the model makes.
AIAgent agent = baseAgent
    .AsBuilder()
    .Use(runFunc: RunMiddlewares.Logging(), runStreamingFunc: null)
    .Use(runFunc: RunMiddlewares.Redaction(), runStreamingFunc: null)
    .Use(ToolApprovalMiddleware.Create())
    .Build();

// Guardrail scenario: the ask carries an employee ID (redaction middleware
// strips it before the model sees it) and asks the agent to close a ticket —
// the approval middleware must ask the operator before the UpdateTicketStatus
// tool with status Closed is allowed to run. Answer y at the prompt to approve,
// anything else to reject.
var ask =
    $"Employee EMP-44555 says the VPN issue on ticket {seeded.Id} is fixed — close it";

Console.WriteLine($"user> {ask}");
var response = await agent.RunAsync(ask);
Console.WriteLine($"bot> {response.Text}");

Console.WriteLine();
Console.WriteLine("--- final store state ---");
foreach (var ticket in await store.ListAsync())
    Console.WriteLine(
        $"{ticket.Id} | {ticket.Status} | {ticket.Priority} | {ticket.Title}" +
        (ticket.Notes.Count == 0 ? "" : $" | notes: {string.Join(" / ", ticket.Notes)}"));
