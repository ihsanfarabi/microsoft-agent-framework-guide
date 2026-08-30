using MafDemo.AgentCommon;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using P06.TriageComposition;

namespace P07.ResolutionWorkflow;

/// <summary>
/// The small local agents that power P07's graph nodes. REUSE, NOT PORT: the
/// diagnosis node wraps the P06 HelpDeskHQ specialists (same
/// <see cref="Specialists"/> factories, same handbook-grounded tooling), while
/// the two nodes the resolution pipeline needs but P06 does not provide — a
/// one-word classifier and an escalation engineer — are new, kept as thin
/// <c>ChatClientAgent</c> constructions on the shared <see cref="OllamaChat"/>
/// factory using the established P02/P06 wiring.
/// </summary>
public static class Agents
{
    /// <summary>Front-desk classifier for the Triage node: answers with exactly
    /// one category word so the graph can route deterministically.</summary>
    public static ChatClientAgent TriageClassifier()
    {
        IChatClient chatClient = new ChatClientBuilder(OllamaChat.Create())
            .Build();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TriageClassifier",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    """
                    You are HelpDeskHQ's front-desk classifier. Classify the user's IT problem
                    as exactly one word: network, software, or hardware. Reply with that single
                    word and nothing else.
                    """,
            },
        });
    }

    /// <summary>Escalation engineer for the Critical detour: refines an
    /// already-proposed fix for incident response.</summary>
    public static ChatClientAgent EscalationEngineer()
    {
        IChatClient chatClient = new ChatClientBuilder(OllamaChat.Create())
            .Build();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "EscalationEngineer",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    """
                    You are HelpDeskHQ's escalation engineer handling Critical incidents.
                    Refine the proposed fix into a concrete, safest-first action plan the
                    on-call operator can execute immediately. Reply with 1-3 sentences.
                    """,
            },
        });
    }

    /// <summary>Picks one of the P06 specialists by the triage classifier's
    /// word; anything unrecognized falls back to the network specialist
    /// (the classifier is prompted to answer one word, but LLM output is a
    /// boundary — the graph must not die on a stray "Network.").</summary>
    public static AIAgent SpecialistFor(string triage, SpecialistTools tools) =>
        triage.Trim().TrimEnd('.').ToLowerInvariant() switch
        {
            "software" => Specialists.SoftwareSpecialist(tools),
            "hardware" => Specialists.HardwareSpecialist(tools),
            _ => Specialists.NetworkSpecialist(tools),
        };
}