using System.Text.Json;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI.Workflows;
using P04.HandbookRag;
using P06.TriageComposition;
using P07.ResolutionWorkflow.Executors;

namespace P07.ResolutionWorkflow;

/// <summary>
/// Public factory for the resolution workflow graph, extracted verbatim from
/// P07's Program.cs (P09 plan, Task 4: the DurableHost needs to build the
/// same graph but Program.cs is a top-level-statement file — internal statics
/// in it aren't reachable). Graph topology and executor ids are exactly the
/// ones P07 has always run with.
/// </summary>
public static class ResolutionWorkflowFacts
{
    // ---- Graph wiring. Conditional edges send the Critical ticket through the
    // escalation node (decision from ApprovalPolicy, kept pure/testable); the
    // approval executor sends FixApprovalRequest along the edge to the
    // RequestPort, and the runtime returns the operator's ApprovalDecision to
    // that same executor (HITL response routing).
    public static Workflow Build(ITicketStore store, HandbookRetriever retriever)
    {
        var port = RequestPort.Create<FixApprovalRequest, ApprovalDecision>("FixApproval");
        var triageExecutor = new TriageExecutor(Agents.TriageClassifier());
        var diagnosticExecutor = new DiagnosticExecutor(new SpecialistTools(store, retriever));
        var escalationExecutor = new EscalationExecutor(Agents.EscalationEngineer());
        var approvalExecutor = new ApprovalExecutor(store);

        return new WorkflowBuilder(triageExecutor)
            .AddEdge(triageExecutor, diagnosticExecutor)
            .AddEdge<object>(diagnosticExecutor, escalationExecutor,
                condition: RouteEscalation)
            .AddEdge<object>(diagnosticExecutor, approvalExecutor,
                condition: RouteApproval)
            .AddEdge(escalationExecutor, approvalExecutor)
            .AddEdge(approvalExecutor, port)      // request: FixApprovalRequest -> port
            .AddEdge(port, approvalExecutor)      // response: ApprovalDecision back
            .WithOutputFrom(approvalExecutor)
            // Stable workflow name: Durable Task registers orchestrations by
            // name (in-process P07 ignores it; P09.DurableHost requires it).
            .WithName("ResolutionWorkflow")
            .Build();
    }

    // The durable edge router hands conditions the executor OUTPUT object when
    // it can type it, else a deserialized `object` (boxed JsonElement). Stage
    // executors are void-executors (Executor<TicketContext>), so the fallback
    // is what conditions see — accept both shapes: the live TicketContext AND
    // a boxed JsonElement to self-parse (durable replay path).
    private static bool RouteEscalation(object? msg) => Decide(msg, wantsEscalation: true);

    private static bool RouteApproval(object? msg) => Decide(msg, wantsEscalation: false);

    private static bool Decide(object? obj, bool wantsEscalation)
    {
        TicketContext? t = obj switch
        {
            TicketContext ctx => ctx,
            JsonElement { ValueKind: JsonValueKind.String } je when je.GetString() is { } s => ParseCtx(s),
            JsonElement je => ParseCtx(je.GetRawText()),
            _ => null,
        };
        if (t is null)
        {
            // Unreadable message (no typed output): route as non-escalating.
            return !wantsEscalation;
        }
        return ApprovalPolicy.NeedsEscalation(t.Priority) == wantsEscalation;
    }

    private static TicketContext? ParseCtx(string json)
    {
        // Durable payload JSON is camelCase (DurableSerialization naming).
        try { return JsonSerializer.Deserialize<TicketContext>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
    }
}