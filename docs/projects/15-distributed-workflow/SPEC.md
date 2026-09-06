# SPEC — P15: DistributedWorkflow (MAF graph workflow spanning processes via A2A)

**Tier:** Advanced · **Estimate:** 6–8 hours · **Depends on:** P07, P09

## Story

One deterministic `WorkflowBuilder` graph on a local host whose middle nodes
are **remote A2A agents**: a local triage executor feeds `DiagnosisAgent`
(A2A @ :5200), a conditional edge decides whether `InventoryAgent` (existing
P09 service @ :5199) is consulted at all, and a local reporter prints the
final answer. Aspire dashboard shows one workflow trace crossing three
processes.

## Success criteria

- Two remote hops execute inside one graph; conditional edge provably skips the second hop for a software-only ticket (graph decides, not the LLM).
- Killing the inventory service mid-flight surfaces the A2A failure as a `WorkflowErrorEvent` (visible, not swallowed).
- No official MAF doc page/sample combines A2A client + WorkflowBuilder — NOTES records the by-contract finding with source links.

## Topology

```
OrchestratorHost (console)
  triageExecutor  ──▶  DiagnosisAgent  (@localhost:5200, A2A)
                   conditional edge: needs hardware?
                   ──▶  InventoryAgent (@localhost:5199, existing P09 service)
                   ──▶  reportExecutor (local, yields final answer)
```

## Verified facts (P15 research)

- `A2AAgent : AIAgent` from `new A2ACardResolver(new Uri(host)).GetAIAgentAsync()`; any `AIAgent` binds as workflow executor via the implicit conversion `AIAgent → ExecutorBinding` (`Microsoft.Agents.AI.Workflows/ExecutorBinding.cs`). Docs pattern: `new WorkflowBuilder(agentA).AddEdge(agentA, agentB).Build()` — docs use remote Foundry agents, so an A2A client satisfies the same contract.
- P09 infra reused verbatim: `InventoryAgentService` (A2A host: `AddKeyedSingleton<AIAgent>` + `AddA2AServer` + `MapA2AHttpJson` + `MapWellKnownAgentCard`, port 5199), `HelpDeskClient` (`A2ACardResolver.GetAIAgentAsync`), `P07`'s conditional-edge style.
- Packages already pinned and compatible: `Microsoft.Agents.AI.A2A` + `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` 1.19.0-preview.260822.1, `Microsoft.Agents.AI.Workflows` 1.19.0.
- Residual risk: the agent-executor boundary runs over the ChatProtocol (`string`/`ChatMessage`/`AgentResponse`) — `A2AAgent.RunCoreAsync` accepts it but no official sample wires `AddEdge` this way; expect an afternoon of type-inference friction. Fallback: 10-line custom `Executor` wrapping `remoteAgent.RunAsync`.
- No durability in v1: checkpointing a graph whose nodes make remote HTTP calls is untested in MAF — explicitly out of scope (future P16).

## Non-goals

No DTS/durable, no changes to P09 services (InventoryAgent consumed as-is), no new MAF packages beyond those P09 already pins.

## Resources

- https://learn.microsoft.com/en-us/agent-framework/workflows/agents-in-workflows
- https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/a2a/server
- Samples: `dotnet/samples/02-agents/A2A/` (client-as-tool), `dotnet/samples/04-hosting/FoundryHostedAgents/responses/Hosted-Workflow-Handoff` (cross-process, Foundry-only)