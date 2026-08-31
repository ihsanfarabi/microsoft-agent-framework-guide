using MafDemo.AgentCommon;
using MafDemo.Core.Handbook;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using P04.HandbookRag;
using P10.HelpDeskCapstone.Agents;

using A2A;
using A2A.AspNetCore;
using MafDemo.AgentCommon;
using MafDemo.Core.Handbook;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using P04.HandbookRag;
using P10.HelpDeskCapstone.Agents;

var builder = WebApplication.CreateBuilder(args);
// Port comes from ASPNETCORE_URLS (compose: http://+:8080) or the
// launchSettings profile (local `dotnet run`: http://localhost:5080).

// OTLP traces to the Aspire dashboard (same wiring as P05-P09; the compose
// stack Task 5 maps the dashboard OTLP receiver).
Telemetry.StartOtlp("P10.HelpDeskCapstone");

// The RAG agent over the shared Ollama chat factory — the P04 factory with
// the HandbookContextProvider grounding (agent Name "HandbookBot"; the hosted
// registration key must match the agent's Name exactly).
var retriever = AgentsSetup.BuildRetriever(out var chunkCount);
Console.WriteLine($"indexed {chunkCount} handbook chunks");

var handbookAgent = builder.AddAIAgent("HandbookBot", (_, _) => HandbookBot.Create(retriever));

// Declarative agents from agents/*.yaml — ChatClientAgent on the same Ollama
// chat client (YAML kind: Prompt definitions; registration key = YAML name,
// which AddAIAgent requires to equal the loaded agent's Name exactly).
IChatClient ollama = OllamaChat.Create();
var yamlAgents = await YamlAgents.LoadAllAsync(AgentsDirectory(), ollama);
foreach (var agent in yamlAgents.Values)
    builder.AddAIAgent(agent.Name, (_, _) => agent);

builder.Services.AddKeyedSingleton<AIAgent>("FaqBot", (_, _) => yamlAgents["FaqBot"]);
builder.AddA2AServer("FaqBot");

var app = builder.Build();

// OpenAI-compatible Chat Completions endpoint for the handbook agent.
app.MapOpenAIChatCompletions(handbookAgent, "/v1/chat/completions");

// A2A endpoint for the YAML FaqBot — same binding + card pattern P09
// verified (card at /.well-known/agent-card.json).
app.MapA2AHttpJson("FaqBot", "/a2a/faq");
app.MapWellKnownAgentCard(new AgentCard
{
    Name = "FaqBot",
    Description = "HelpDeskHQ FAQ bot (declarative YAML agent).",
    Version = "1.0",
    SupportedInterfaces = [new AgentInterface
    {
        Url = "http://localhost:5080/a2a/faq",
        ProtocolBinding = ProtocolBindingNames.HttpJson,
        ProtocolVersion = "1.0",
    }],
});

var hostedAgentNames = yamlAgents.Keys.Append("HandbookBot").ToArray();
app.MapGet("/health", () => Results.Ok(new { status = "ok", agents = hostedAgentNames }));

app.Run();

// agents/ ships next to the binary (csproj CopyToOutputDirectory), so the
// AppContext.BaseDirectory location works for dotnet run and the published
// Docker image alike.
static string AgentsDirectory() => Path.Combine(AppContext.BaseDirectory, "Definitions");

public partial class Program;