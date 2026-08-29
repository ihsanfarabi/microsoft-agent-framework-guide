using MafDemo.AgentCommon;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// HelpDeskHQ ticket agent: a <see cref="ChatClientAgent"/> wired to Ollama
/// (via the shared <see cref="OllamaChat"/> factory, so tracing and config
/// resolution are preserved) whose four ticket tools from
/// <see cref="TicketToolFunctions"/> are registered as AIFunctions.
/// <see cref="CreateWithMcp"/> additionally merges tools discovered from an
/// MCP server into the same tool set.
/// </summary>
public static class TicketBot
{
    /// <summary>Agent with the four built-in ticket function tools only.</summary>
    public static ChatClientAgent Create(ITicketStore store)
        => CreateCore(store, []);

    /// <summary>
    /// Agent with the built-in ticket function tools plus tools surfaced by
    /// an MCP server (already converted to <see cref="AITool"/> via
    /// <c>mcpTools.Cast&lt;AITool&gt;()</c> by the caller).
    /// </summary>
    public static ChatClientAgent CreateWithMcp(ITicketStore store, IEnumerable<AITool> mcpTools)
        => CreateCore(store, mcpTools);

    private static ChatClientAgent CreateCore(ITicketStore store, IEnumerable<AITool> mcpTools)
    {
        var tools = new TicketToolFunctions(store);

        // UseFunctionInvocation is required: it runs the client-side tool
        // loop (model requests a call -> function executes -> result goes
        // back to the model) that plain IChatClient does not provide.
        IChatClient chatClient = new ChatClientBuilder(OllamaChat.Create())
            .UseFunctionInvocation()
            .Build();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "TicketBot",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    """
                    You are HelpDeskHQ's ticket bot. Create, list, and update support tickets
                    by calling the provided tools — never invent ticket IDs, always echo
                    back the ticket ID returned by a tool. Valid statuses: Open, InProgress,
                    Resolved, Closed. Valid priorities: Low, Normal, High, Critical.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(tools.CreateTicketAsync),
                    AIFunctionFactory.Create(tools.ListTicketsAsync),
                    AIFunctionFactory.Create(tools.UpdateTicketStatusAsync),
                    AIFunctionFactory.Create(tools.AddTicketNoteAsync),
                    // MCP server tools (already AITool instances) ride alongside
                    // the local function tools in the same tool set.
                    .. mcpTools,
                ],
            },
        });
    }
}
