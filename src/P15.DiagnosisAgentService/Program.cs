using A2A;
using A2A.AspNetCore;
using MafDemo.AgentCommon;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;

// OTLP tracing to the Aspire dashboard (same wiring as P09). The A2A request
// from the P15 orchestrator must show up as a cross-process span.
using var telemetry = Telemetry.StartOtlp("P15.DiagnosisAgentService");

var builder = WebApplication.CreateBuilder(args);

// P10 lesson: NO UseUrls() here — a hardcoded URL silently overrides
// ASPNETCORE_URLS. The port comes from configuration only (ASPNETCORE_URLS
// in shell runs, applicationUrl in launchSettings).

// Free-form diagnosis: no tools, no session state — the orchestrator owns
// sequencing (P15 requirement). Each A2A message is answered independently.
var client = OllamaChat.Create();
var diagnosis = new ChatClientAgent(client, name: "DiagnosisAgent",
    instructions: """
        You diagnose IT tickets. Answer in ≤ 3 sentences. If the diagnosis
        mentions hardware, say NEEDS-HARDWARE.
        """);

// A2A server plumbing resolves the agent as a keyed AIAgent service by name.
builder.Services.AddKeyedSingleton<AIAgent>("diagnosis", (_, _) => diagnosis);
builder.AddA2AServer("diagnosis");

var app = builder.Build();
app.MapA2AHttpJson("diagnosis", "/a2a/diagnosis"); // HTTP+JSON binding
app.MapWellKnownAgentCard(new AgentCard
{
    Name = "DiagnosisAgent",
    Description = "Diagnoses IT tickets; flags NEEDS-HARDWARE when hardware is implicated.",
    Version = "1.0",
    SupportedInterfaces = [new AgentInterface
    {
        Url = "http://localhost:5200/a2a/diagnosis",
        ProtocolBinding = ProtocolBindingNames.HttpJson,
        ProtocolVersion = "1.0",
    }],
});
app.Run();
