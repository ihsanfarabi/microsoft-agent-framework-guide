using MafDemo.AgentCommon;
using P11.StructuredOutput;

// Start OTel tracing first so the provider is listening before any model call.
// Disposed on exit, which flushes the spans to the console exporter.
using var telemetry = Telemetry.Start("P11.StructuredOutput");

var agent = TypedTriage.TypedTriageAgent(OllamaChat.Create());

// Typed run: MAF requests structured output from the model and deserializes
// the response into TriageDecision for us — no manual JSON parsing. JsonOptions
// is case-insensitive + enum-tolerant because Ollama models emit camelCase.
TriageDecision decision = (await agent.RunAsync<TriageDecision>(
    "Laptop won't boot, deadline tomorrow",
    serializerOptions: TypedTriage.JsonOptions)).Result;

Console.WriteLine($"Category: {decision.Category}");
Console.WriteLine($"Priority: {decision.Priority}");
Console.WriteLine($"Summary:  {decision.Summary}");