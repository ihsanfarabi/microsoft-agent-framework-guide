using A2A;
using MafDemo.AgentCommon;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using P15.OrchestratorHost.Executors;

// ---- Telemetry: OTLP to the Aspire dashboard (same wiring as P09/P10 —
// aspire-dashboard.sh, traces at http://localhost:18888), so one workflow run
// shows its A2A calls to two different remote targets. Three activity sources
// matter here: the shared agent source, the workflow engine's own source
// ("Microsoft.Agents.AI.Workflows", decompiled from 1.19.0), and the A2A
// client SDK's ("A2A") — each emits on its own source name, and unregistered
// sources are dropped. P15_TRACE_CONSOLE=1 additionally mirrors every span to
// stdout so a run transcript carries the trace even without a watching
// dashboard.
var telemetryBuilder = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("P15.OrchestratorHost"))
    .AddSource(Telemetry.SourceName)
    .AddSource("Microsoft.Agents.AI.Workflows")
    .AddSource("A2A")
    .AddOtlpExporter(o => o.Endpoint = new Uri(
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            ?? Telemetry.DefaultOtlpEndpoint));
if (Environment.GetEnvironmentVariable("P15_TRACE_CONSOLE") == "1")
{
    telemetryBuilder.AddConsoleExporter();
}
using var telemetry = telemetryBuilder.Build();

// Step 1: discover both remote agents off their well-known agent cards (P09
// HelpDeskClient pattern — the resolver takes the host base URI only; the
// card path is the package default /.well-known/agent-card.json).
AIAgent diagnosis = await new A2ACardResolver(new Uri("http://localhost:5200")).GetAIAgentAsync();
AIAgent inventory = await new A2ACardResolver(new Uri("http://localhost:5199")).GetAIAgentAsync();
Console.WriteLine($"[discovered] {diagnosis.Name} (localhost:5200) and {inventory.Name} (localhost:5199)");

// Step 3: two scenarios in one run of the program. A is software-only and
// must provably SKIP the inventory hop; B implicates hardware and takes both
// remote hops. The conditional edge (graph, not the LLM) makes the decision.
(string Label, string Ticket)[] scenarios =
[
    ("A", "IT ticket: since yesterday's update the accounting app crashes on startup every time it is opened. No error code is shown."),
    ("B", "IT ticket: my laptop will not power on at all. The charger LED is dark and the battery shows no charge after an hour plugged in."),
];

foreach (var (label, ticket) in scenarios)
{
    Console.WriteLine();
    Console.WriteLine($"=== scenario {label} ===");
    Console.WriteLine($"[ticket] {ticket}");
    await RunScenarioAsync(diagnosis, inventory, ticket);
}

return 0;

// ---- One graph run per scenario. A Workflow is owned by its first runner
// (P07 NOTES), so each scenario rebuilds the graph — identical topology and
// executor ids, fresh closures for the edge conditions.
static async Task RunScenarioAsync(AIAgent diagnosis, AIAgent inventory, string ticket)
{
    // Direct route (SPEC's primary option): the AIAgent binds as a workflow
    // executor via the implicit AIAgent -> ExecutorBinding conversion
    // (ExecutorBinding.cs in 1.19.0). Constructed explicitly here only to set
    // EmitAgentResponseEvents, so each remote hop's AgentResponse surfaces as
    // a WorkflowOutputEvent carrying the yielding executor's id — that is how
    // the run transcript names which remote process produced which answer.
    // No wrapper executor needed: the ChatProtocol boundary types
    // (List<ChatMessage> out, ChatMessage + TurnToken in) were resolved by
    // decompiling AIAgentHostExecutor / ChatProtocolExecutor / DirectEdgeRunner.
    var diagnosisNode = new AIAgentBinding(diagnosis, new AIAgentHostOptions { EmitAgentResponseEvents = true });
    var inventoryNode = new AIAgentBinding(inventory, new AIAgentHostOptions { EmitAgentResponseEvents = true });
    var triage = new TriageExecutor();
    var report = new ReportExecutor();

    // Set by the response-list branch of the inventory edge; the agent node's
    // trailing TurnToken then rides the same edge to trigger the inventory
    // turn. On the software path it stays false, so the TurnToken is dropped
    // at the condition and the remote inventory agent is NEVER invoked — the
    // skip is in the graph, not in the model.
    bool hardwareDetected = false;

    Workflow workflow = new WorkflowBuilder(triage)
        .AddEdge(triage, diagnosisNode)
        .AddEdge<object>(diagnosisNode, inventoryNode, msg =>
        {
            if (msg is List<ChatMessage> result && IsAgentResult(result) && ContainsHardware(result))
            {
                hardwareDetected = true;
                Console.WriteLine("[route] DiagnosisAgent (:5200) -> InventoryAgent (:5199): diagnosis flags NEEDS-HARDWARE");
            }
            return hardwareDetected;
        })
        .AddEdge<List<ChatMessage>>(diagnosisNode, report, result =>
        {
            if (result is null || !IsAgentResult(result) || ContainsHardware(result))
            {
                return false;
            }
            Console.WriteLine("[route] DiagnosisAgent (:5200) -> Report (software-only: inventory hop SKIPPED)");
            return true;
        })
        .AddEdge(inventoryNode, report)
        .WithOutputFrom(diagnosisNode, inventoryNode, report)
        .Build();

    // `RunAsync(workflow, input)` from the plan snippet does not exist in
    // 1.19.0 (only the streaming variants) — same event loop as P07.
    await using StreamingRun handle = await InProcessExecution.RunStreamingAsync(workflow, ticket);
    await foreach (WorkflowEvent evt in handle.WatchStreamAsync())
    {
        switch (evt)
        {
            case WorkflowOutputEvent { Data: AgentResponse response } output:
                Console.WriteLine($"[hop output] {output.ExecutorId}: {response.Text}");
                break;
            case WorkflowOutputEvent output:
                Console.WriteLine($"[done] {output.Data}");
                break;
            case WorkflowErrorEvent error:
                Console.Error.WriteLine($"[workflow error] {error.Exception}");
                break;
            case ExecutorFailedEvent failure:
                Console.Error.WriteLine($"[executor failed: {failure.ExecutorId}] {failure.Data}");
                break;
        }
    }
}

// The diagnosis node forwards what it received (reassigned to the user role)
// ahead of its own response; only a list containing an assistant message is
// the remote result (A2A Role.Agent maps to ChatRole.Assistant in the A2A
// client package — verified in AIContentExtensions).
static bool IsAgentResult(List<ChatMessage> messages) =>
    messages.Any(m => m.Role == ChatRole.Assistant);

static bool ContainsHardware(List<ChatMessage> messages) =>
    messages.Any(m => m.Text?.Contains("NEEDS-HARDWARE", StringComparison.OrdinalIgnoreCase) == true);
