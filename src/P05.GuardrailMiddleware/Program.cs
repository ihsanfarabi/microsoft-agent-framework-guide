using MafDemo.AgentCommon;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using P05.GuardrailMiddleware;
using P05.GuardrailMiddleware.Middleware;

// Start OTel tracing first so the provider is listening before any model call.
// The redaction middleware strips PII before the messages reach the agent, so
// what the spans capture for the model input is the redacted text.
using var telemetry = Telemetry.Start("P05.GuardrailMiddleware");

var store = new InMemoryTicketStore();
var baseAgent = TicketAgent.Create(store);

// Wrap the base agent in the two run middlewares. Only the non-streaming
// run func is provided per middleware — the 1.19.0 builder reuses it for
// both RunAsync and RunStreamingAsync.
AIAgent agent = baseAgent
    .AsBuilder()
    .Use(runFunc: RunMiddlewares.Logging(), runStreamingFunc: null)
    .Use(runFunc: RunMiddlewares.Redaction(), runStreamingFunc: null)
    .Build();

// Guardrail scenario: the ask contains an employee ID and an email address.
// The redaction middleware must strip both before the model ever sees the
// input (the ticket created from it will carry [REDACTED-*] placeholders),
// while the logging middleware brackets the run with [log] lines.
var ask =
    "Employee EMP-44555 asks: what is the wifi password policy? " +
    "Wait, also create a ticket for my VPN issue, priority high, " +
    "my email is jane.doe@contoso.com";

Console.WriteLine($"user> {ask}");
var response = await agent.RunAsync(ask);
Console.WriteLine($"bot> {response.Text}");

Console.WriteLine();
Console.WriteLine("--- final store state ---");
foreach (var ticket in await store.ListAsync())
    Console.WriteLine(
        $"{ticket.Id} | {ticket.Status} | {ticket.Priority} | {ticket.Title}" +
        (ticket.Notes.Count == 0 ? "" : $" | notes: {string.Join(" / ", ticket.Notes)}"));
