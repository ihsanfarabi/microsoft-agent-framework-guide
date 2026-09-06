# SPEC — P09: A2aDurable (Remote Agent + Durable Workflow)

**Tier:** Advanced · **Estimate:** 6–8 hours · **Depends on:** P08

## Goal

Two production mechanics in one project: (1) an **inventory agent running as
its own service**, reached over the A2A protocol by the helpdesk agent; (2) the
P07 resolution workflow made **durable** — kill the host mid-workflow, restart,
it resumes from checkpoint. First taste of multi-service agents + reliable execution.

## Concepts learned

- A2A protocol: agent card discovery, JSON-RPC task exchange
- A2A server self-host: `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`, `MapA2A`
- A2A client: `Microsoft.Agents.AI.A2A`, `A2AAgent` wraps remote endpoint as `AIAgent`
- Durable Extension bring-your-own-compute: Durable Task Scheduler (local emulator), `ConfigureDurableWorkflows`, worker + client builders, checkpoint recovery

## Requirements

1. `src/P09.InventoryAgentService` — ASP.NET Core app exposing `InventoryAgent` at `/a2a/inventory` (A2A protocol + agent card), with tools `check_stock`, `reserve_laptop` over a deterministic fake `InMemoryInventoryStore`.
2. `src/P09.HelpDeskClient` — console agent that discovers the remote agent (well-known URI / agent card) via `A2AAgent` and calls it: "loaner laptop for ticket X" resolves stock + reservation through the remote service.
3. `src/P09.DurableHost` — generic host running P07's resolution workflow on the Durable Extension (local Durable Task Scheduler emulator via Docker), registered with `ConfigureDurableWorkflows`.
4. Demo script: start workflow, kill host (`Ctrl-C`) mid-workflow, restart, workflow resumes from checkpoint and completes.
5. Inventory store logic xUnit-tested; two-process A2A flow smoke-tested by scripted scenario.
6. Trace check: A2A call visible as spans in Aspire dashboard (OTLP).

## Success criteria

- `curl` the agent card endpoint returns card JSON; A2A conversation completes across process boundary.
- Loaner-laptop scenario answered with real stock data from the other process.
- Durable workflow: kill mid-run, restart, resumes and completes — no step re-executed twice (verify via trace).
- Inventory unit tests pass.

## Stretch

- Move durable workflow to Azure Functions locally (Azurite + func CLI) — same definition, different host.
- Secure A2A endpoint with API key auth.

## Resources

- A2A hosting .NET (verified): https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/a2a/dotnet
- A2A agent client: https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/agent-services/a2a
- Durable Extension (verified): https://learn.microsoft.com/en-us/agent-framework/hosting/azure-functions
- Durable workflows blog: https://devblogs.microsoft.com/dotnet/durable-workflows-in-the-microsoft-agent-framework
- Console samples: https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/04-hosting