using MafDemo.AgentCommon;
using MafDemo.Core.Handbook;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using P04.HandbookRag;
using P10.HelpDeskCapstone.Agents;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5080");

// OTLP traces to the Aspire dashboard (same wiring as P05-P09; the compose
// stack Task 5 maps the dashboard OTLP receiver).
Telemetry.StartOtlp("P10.HelpDeskCapstone");

// The RAG agent over the shared Ollama chat factory — the P04 factory with
// the HandbookContextProvider grounding (agent Name "HandbookBot"; the hosted
// registration key must match the agent's Name exactly).
var retriever = AgentsSetup.BuildRetriever(out var chunkCount);
Console.WriteLine($"indexed {chunkCount} handbook chunks");

var handbookAgent = builder.AddAIAgent("HandbookBot", (_, _) => HandbookBot.Create(retriever));

var app = builder.Build();

// OpenAI-compatible Chat Completions endpoint for the handbook agent.
app.MapOpenAIChatCompletions(handbookAgent, "/v1/chat/completions");

app.MapGet("/health", () => Results.Ok(new { status = "ok", agents = new[] { "HandbookBot" } }));

app.Run();

public partial class Program;