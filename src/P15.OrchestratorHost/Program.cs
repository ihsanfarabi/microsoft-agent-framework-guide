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

// Optional scenario selector: `dotnet run -- B` runs only scenario B. The
// failure demo (scripts/demo15-failure.sh) uses this so the failing run and
// the still-succeeds run are separate process invocations — under the
// PROPAGATE decision below, one failed run exits non-zero and never rolls
// into the next scenario in the same process.
string[] only = args.Select(a => a.Trim().ToUpperInvariant())
                    .Where(a => a is "A" or "B")
                    .Distinct()
                    .ToArray();

// Step 1: discover both remote agents off their well-known agent cards (P09
// HelpDeskClient pattern — the resolver takes the host base URI only; the
// card path is the package default /.well-known/agent-card.json).
AIAgent diagnosis = await DiscoverAsync("DiagnosisAgent", new Uri("http://localhost:5200"), "/a2a/diagnosis");
AIAgent inventory = await DiscoverAsync("InventoryAgent", new Uri("http://localhost:5199"), "/a2a/inventory");

// Step 3: two scenarios in one run of the program. A is software-only and
// must provably SKIP the inventory hop; B implicates hardware and takes both
// remote hops. The conditional edge (graph, not the LLM) makes the decision.
(string Label, string Ticket)[] scenarios =
[
    ("A", "IT ticket: since yesterday's update the accounting app crashes on startup every time it is opened. No error code is shown."),
    ("B", "IT ticket: my laptop will not power on at all. The charger LED is dark and the battery shows no charge after an hour plugged in."),
];

try
{
    foreach (var (label, ticket) in scenarios)
    {
        if (only.Length > 0 && !only.Contains(label))
        {
            continue;
        }

        Console.WriteLine();
        Console.WriteLine($"=== scenario {label} ===");
        Console.WriteLine($"[ticket] {ticket}");
        await RunScenarioAsync(diagnosis, inventory, ticket);
    }
}
catch (WorkflowFailedException failure)
{
    // HANDLE-OR-PROPAGATE DECISION: PROPAGATE (see the long comment in
    // RunScenarioAsync). The catch here exists ONLY to print the diagnostics
    // an operator needs and exit non-zero — no retry (retries/transient-fault
    // policy is a durable-workflow-host concern; noted for P16), and no
    // continue-to-the-next-scenario: a failed scenario terminates the run.
    // The original exception rides along as InnerException — printed in full,
    // never swallowed or rethrow-mangled.
    Console.Error.WriteLine();
    Console.Error.WriteLine($"[FAILED] scenario did not complete: {failure.Message}");
    Console.Error.WriteLine($"[FAILED] original exception: {failure.InnerException}");
    return 1;
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
    var hardwareGate = new HardwareGateExecutor();
    var report = new ReportExecutor();

    Workflow workflow = new WorkflowBuilder(triage)
        .AddEdge(triage, diagnosisNode)
        // Both conditional edges are pure content predicates (P07 style): no
        // prints, no closure state. The hardware path goes through the
        // HardwareGate node, which performs the TurnToken handshake the
        // inventory node needs; the trailing TurnToken an agent node emits is
        // dropped at these typed conditions (null-safe predicates), so on the
        // software path the remote inventory agent is NEVER invoked.
        .AddEdge<List<ChatMessage>>(diagnosisNode, hardwareGate, NeedsHardware)
        .AddEdge<List<ChatMessage>>(diagnosisNode, report, SoftwareOnly)
        .AddEdge(hardwareGate, inventoryNode)
        .AddEdge(inventoryNode, report)
        .WithOutputFrom(diagnosisNode, inventoryNode, report)
        .Build();

    // `InProcessExecution.RunAsync(workflow, input)` DOES exist in 1.19.0
    // (returns ValueTask<Run>); we use the streaming variant for the
    // P07-style event loop, which surfaces failures as WorkflowErrorEvent.
    await using StreamingRun handle = await InProcessExecution.RunStreamingAsync(workflow, ticket);
    bool diagnosisHit = false;
    bool inventoryHit = false;
    WorkflowErrorEvent? errorEvent = null;
    await foreach (WorkflowEvent evt in handle.WatchStreamAsync())
    {
        switch (evt)
        {
            case WorkflowOutputEvent { Data: AgentResponse response } output:
                if (output.ExecutorId.StartsWith("InventoryAgent", StringComparison.Ordinal))
                {
                    inventoryHit = true;
                }
                else if (output.ExecutorId.StartsWith("DiagnosisAgent", StringComparison.Ordinal))
                {
                    diagnosisHit = true;
                }
                Console.WriteLine($"[hop output] {output.ExecutorId}: {response.Text}");
                break;
            case WorkflowOutputEvent output:
                Console.WriteLine($"[done] {output.Data}");
                break;
            case WorkflowErrorEvent error:
                // Streaming-mode failure surface: a remote hop dying mid-run
                // arrives here as a WorkflowErrorEvent carrying the original
                // exception (no rethrow-mangling — it is printed verbatim).
                errorEvent = error;
                Console.Error.WriteLine($"[workflow error] {error.Exception}");
                break;
            case ExecutorFailedEvent failure:
                Console.Error.WriteLine($"[executor failed: {failure.ExecutorId}] {failure.Data}");
                break;
        }
    }

    if (errorEvent is not null)
    {
        // HANDLE-OR-PROPAGATE DECISION: PROPAGATE. The event loop has already
        // printed the raw exception ([workflow error] above). We do NOT retry
        // and we do NOT fall through to the next scenario as if nothing
        // happened — retries against a dead remote hop are a policy concern
        // for a durable workflow host (P16 note), not for this console
        // orchestrator. Instead the original exception is wrapped (InnerException
        // preserved, message verbatim in the wrapper text) with the one thing
        // a raw socket error may not make obvious: WHICH hop died, i.e. which
        // A2A endpoint the workflow was calling. The wrapper propagates to the
        // top-level handler, which prints it and exits non-zero.
        string failingHop = inventoryHit
            ? "Report (local) — after the InventoryAgent hop"
            : diagnosisHit
                ? "InventoryAgent — A2A endpoint http://localhost:5199/a2a/inventory (P09 InventoryAgentService on port 5199)"
                : "DiagnosisAgent — A2A endpoint http://localhost:5200/a2a/diagnosis (DiagnosisAgentService on port 5200)";
        Console.Error.WriteLine($"[failed hop] {failingHop}");
        Exception original = errorEvent.Exception
            ?? new InvalidOperationException("the workflow reported an error without an exception");
        throw new WorkflowFailedException(failingHop, original);
    }

    // Route summary derived from what actually ran (no condition side
    // effects): an InventoryAgent output event means the second remote hop
    // fired; its absence means the conditional edge skipped it.
    Console.WriteLine(inventoryHit
        ? "[route summary] hops hit: Triage (local) -> DiagnosisAgent (:5200) -> InventoryAgent (:5199) -> Report (local)"
        : "[route summary] hops hit: Triage (local) -> DiagnosisAgent (:5200) -> Report (local); InventoryAgent (:5199) SKIPPED by the conditional edge");
}

// The diagnosis node forwards what it received (reassigned to the user role)
// ahead of its own response; only a list containing an assistant message is
// the remote result (A2A Role.Agent maps to ChatRole.Assistant in the A2A
// client package — verified in AIContentExtensions). The forwarded chatter is
// user-role only, so it can never satisfy these predicates.
static bool IsAgentResult(List<ChatMessage> messages) =>
    messages.Any(m => m.Role == ChatRole.Assistant);

static bool NeedsHardware(List<ChatMessage>? result) =>
    result is not null && IsAgentResult(result) && ContainsHardware(result);

static bool SoftwareOnly(List<ChatMessage>? result) =>
    result is not null && IsAgentResult(result) && !ContainsHardware(result);

// Matched against assistant-authored messages ONLY: a ticket or forwarded
// user content that merely mentions the token must not route the graph.
static bool ContainsHardware(List<ChatMessage> messages) =>
    messages.Any(m => m.Role == ChatRole.Assistant &&
                      m.Text?.Contains("NEEDS-HARDWARE", StringComparison.OrdinalIgnoreCase) == true);

// Card discovery with an honest down-service fallback. When the remote
// service is STOPPED the resolver cannot fetch /.well-known/agent-card.json —
// but aborting there would move the failure to startup, before the workflow
// even runs, and the failure-visibility point of this task is precisely that
// a dead remote hop dies INSIDE the workflow and surfaces mid-run as a
// WorkflowErrorEvent. So when the card is unreachable we bind the configured
// endpoint by hand (same URL the service maps: MapA2AHttpJson("inventory",
// "/a2a/inventory") → HTTP+JSON binding) and let the first real A2A call
// produce the genuine connection error at the hop. Nothing is retried or
// masked: the fallback only substitutes the discovery round-trip, and the
// [discovery failed] line says so in the transcript.
static async Task<AIAgent> DiscoverAsync(string name, Uri host, string a2aPath)
{
    try
    {
        AIAgent agent = await new A2ACardResolver(host).GetAIAgentAsync();
        Console.WriteLine($"[discovered] {agent.Name} ({host.Authority}) via agent card");
        return agent;
    }
    // Narrow catch (review fix): only a TRANSPORT failure may fall back to the
    // hand-built card. The A2A SDK's resolver wraps connection errors as
    // A2AException("HTTP request failed", HttpRequestException) — decompiled
    // from A2ACardResolver.GetAgentCardAsync in a2a 1.0.0-preview2 — so the
    // filter matches that shape (and a bare HttpRequestException, should the
    // resolver ever surface one directly). A live-but-sick service that serves
    // a malformed/unparseable card throws A2AException("Failed to parse
    // JSON: …") with an InnerException that is a JsonException, NOT an
    // HttpRequestException — that propagates, as does OperationCanceledException
    // (never caught here): a sick discovery endpoint must not be silently
    // absorbed into a fallback that papers over it.
    catch (Exception ex) when (ex is HttpRequestException
                               || (ex is A2AException a2a && a2a.InnerException is HttpRequestException))
    {
        Console.WriteLine($"[discovery failed] {name} at {host}: {ex}");
        Console.WriteLine($"[discovery fallback] binding configured endpoint {host.GetLeftPart(UriPartial.Authority)}{a2aPath} anyway — the failure will surface at the hop, inside the workflow");
        var card = new AgentCard
        {
            Name = name,
            SupportedInterfaces =
            [
                new AgentInterface { ProtocolBinding = "HTTP+JSON", Url = $"{host.GetLeftPart(UriPartial.Authority)}{a2aPath}" },
            ],
        };
        return card.AsAIAgent();
    }
}

/// <summary>
/// Raised when the event loop observed a <see cref="WorkflowErrorEvent"/>.
/// The original exception is preserved untouched as <see cref="Exception.InnerException"/>
/// (never swallowed, never rethrow-mangled); the wrapper text adds the hop
/// annotation — which A2A endpoint the workflow was calling when it died — so
/// an operator can see WHICH hop failed even when the raw socket error only
/// says "connection refused".
/// </summary>
internal sealed class WorkflowFailedException(string failingHop, Exception inner)
    : Exception($"workflow failed at: {failingHop}. Original exception: [{inner.GetType().Name}] {inner.Message}", inner);
