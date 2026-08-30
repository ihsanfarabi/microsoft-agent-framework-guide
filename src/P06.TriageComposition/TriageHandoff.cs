using MafDemo.AgentCommon;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace P06.TriageComposition;

/// <summary>
/// Handoff-orchestration triage composition: the same three Task 1 specialists
/// plus a front-desk triage agent, wired as a mesh of handoff relationships
/// instead of agents-as-tools. Handoff transfers the WHOLE conversation — the
/// receiving agent owns the task — and control returns to the caller as soon
/// as an agent answers without invoking a handoff tool.
///
/// Verified API (Microsoft.Agents.AI.Workflows 1.19.0, shipped XML + the MAF
/// handoff doc, 2026-07-29):
/// - Package <c>Microsoft.Agents.AI.Workflows</c> 1.19.0 (handoff builders live
///   here, NOT in a separate "Orchestration" package).
/// - Builder: <see cref="AgentWorkflowBuilder.CreateHandoffBuilderWith(AIAgent)"/>
///   or <c>new HandoffWorkflowBuilder(AIAgent initial)</c>, then
///   <see cref="HandoffWorkflowBuilderCore{T}.WithHandoff"/> /
///   <see cref="HandoffWorkflowBuilderCore{T}.WithHandoffs"/>, then
///   <see cref="HandoffWorkflowBuilderCore{T}.Build"/>. Handoff targets are
///   declared as source→target agent pairs; each declared target gets an
///   auto-injected handoff tool whose description carries the configured
///   reason (derived from the target's description/name when no explicit
///   reason is passed). There is no <c>AddAgent(agent, handoffTargets:)</c>
///   — the brief's sketch, corrected here.
/// - Run: <c>InProcessExecution.RunStreamingAsync(workflow, messages)</c> +
///   <c>run.TrySendMessageAsync(new TurnToken(emitEvents: true))</c>, then
///   consume <c>run.WatchStreamAsync()</c> (<see cref="AgentResponseUpdateEvent"/>
///   per agent token, terminal <see cref="WorkflowOutputEvent"/> carrying the
///   merged <see cref="ChatMessage"/> list). Interactive per the handoff doc:
///   a run ends when the holding agent answers without handing off, control
///   returns to the caller, and the caller supplies the next user turn by
///   appending to <c>messages</c> and running again. Handoff tool calls are
///   filtered out of forwarded history; only user/agent messages broadcast.
/// </summary>
public static class TriageHandoff
{
    public static Workflow Create(SpecialistTools tools)
    {
        ChatClientAgent network = Specialists.NetworkSpecialist(tools);
        ChatClientAgent software = Specialists.SoftwareSpecialist(tools);
        ChatClientAgent hardware = Specialists.HardwareSpecialist(tools);

        // Triage agent, built on the Task 2 (TriageAsTools) front-desk pattern,
        // but instructing a HANDOFF (transfer of ownership) instead of a tool
        // call: the workflow injects one handoff tool per WithHandoff target,
        // and triage must always transfer rather than diagnose.
        IChatClient chatClient = new ChatClientBuilder(OllamaChat.Create())
            .UseFunctionInvocation()
            .Build();

        ChatClientAgent triage = new(chatClient, new ChatClientAgentOptions
        {
            Name = "TriageAgent",
            Description = "HelpDeskHQ front desk that routes IT problems to specialists",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are HelpDeskHQ's front desk. Classify the user's IT problem, then
                    ALWAYS hand off the conversation to exactly ONE specialist: NetworkSpecialist
                    (Wi-Fi, VPN, internet), SoftwareSpecialist (apps crashing, install, licenses),
                    or HardwareSpecialist (laptop, charger, peripherals). Do not diagnose the
                    problem yourself.
                    """,
            },
        });

        // Handoff target reasons are explicit so the injected handoff tools tell
        // each holder WHEN to transfer (specialists carry no Description in
        // Task 1's factories — deliberately untouched).
        return AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)
            .WithHandoff(triage, network, "Wi-Fi, VPN and connectivity issues.")
            .WithHandoff(triage, software, "Application crashes, installs, licensing.")
            .WithHandoff(triage, hardware, "Laptops, chargers, printers and physical devices.")
            .WithHandoffs([network, software, hardware], triage,
                "Use when the problem turns out to be outside your specialty.")
            .Build();
    }
}