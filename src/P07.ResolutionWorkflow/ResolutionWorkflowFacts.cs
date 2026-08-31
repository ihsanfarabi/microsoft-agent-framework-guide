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
            .AddEdge(diagnosticExecutor, escalationExecutor,
                condition: (TicketContext t) => ApprovalPolicy.NeedsEscalation(t!.Priority))
            .AddEdge(diagnosticExecutor, approvalExecutor,
                condition: (TicketContext t) => !ApprovalPolicy.NeedsEscalation(t!.Priority))
            .AddEdge(escalationExecutor, approvalExecutor)
            .AddEdge(approvalExecutor, port)      // request: FixApprovalRequest -> port
            .AddEdge(port, approvalExecutor)      // response: ApprovalDecision back
            .WithOutputFrom(approvalExecutor)
            .Build();
    }
}