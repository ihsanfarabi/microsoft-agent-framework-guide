using MafDemo.AgentCommon;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P06.TriageComposition;

/// <summary>
/// Agents-as-tools triage composition: the three Task 1 specialists run as
/// named function tools on a front-desk <c>TriageAgent</c>. The triage agent
/// classifies the user's problem and delegates to exactly one specialist; the
/// specialist is a full <see cref="ChatClientAgent"/> — it keeps its own
/// <c>UseFunctionInvocation</c> chat client and its own tools (handbook /
/// ticket), so its inner tool loop runs inside the triage agent's outer loop.
///
/// Verified API (MAF 1.19.0, shipped Microsoft.Agents.AI.xml + package docs):
/// <c>Microsoft.Agents.AI.AIAgentExtensions.AsAIFunction(this AIAgent agent,
/// AIFunctionFactoryOptions? options = null, AgentSession? session = null)</c>
/// — the brief's sketch of <c>AsAITool(name:, description:)</c> does not exist;
/// <see cref="AIAgentExtensions.AsAIFunction(AIAgent, AIFunctionFactoryOptions, AgentSession)"/>
/// is the exact member, and name/description customization goes through
/// <see cref="AIFunctionFactoryOptions.Name"/> / <see cref="AIFunctionFactoryOptions.Description"/>
/// (the wrapped function takes a query string and returns the specialist's
/// response text).
/// </summary>
public static class TriageAsTools
{
    /// <summary>Builds the front-desk triage agent with all three specialists
    /// wired as fixed-name function tools on its <see cref="ChatOptions.Tools"/>
    /// (same placement as the specialists' own tools in Task 1's
    /// <see cref="Specialists.CreateCore"/>).</summary>
    public static ChatClientAgent Create(SpecialistTools tools)
    {
        AIFunction networkTool = Specialists.NetworkSpecialist(tools).AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = "network_connectivity",
            Description = "Wi-Fi, VPN and connectivity issues. Diagnoses using the company IT handbook.",
        });

        AIFunction softwareTool = Specialists.SoftwareSpecialist(tools).AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = "software_support",
            Description = "Application crashes, installs, licensing. Can look up support tickets by GUID.",
        });

        AIFunction hardwareTool = Specialists.HardwareSpecialist(tools).AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = "hardware_support",
            Description = "Laptops, chargers, printers and physical devices. Diagnoses using the company IT handbook.",
        });

        IChatClient chatClient = new ChatClientBuilder(OllamaChat.Create())
            .UseFunctionInvocation()
            .Build();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TriageAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are HelpDeskHQ's front desk. Classify the user's IT problem, then
                    delegate to exactly ONE specialist tool: network_connectivity (Wi-Fi,
                    VPN, internet), software_support (apps crashing, install, licenses),
                    hardware_support (laptop, charger, peripherals). Return the specialist's
                    answer to the user, prefixed with which specialist handled it.
                    """,
                Tools = [networkTool, softwareTool, hardwareTool],
            },
        });
    }
}
