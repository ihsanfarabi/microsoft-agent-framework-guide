using MafDemo.AgentCommon;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// HelpDeskHQ ticket agent: a <see cref="ChatClientAgent"/> wired to Ollama
/// (via the shared <see cref="OllamaChat"/> factory, so tracing and config
/// resolution are preserved) whose four ticket tools from
/// <see cref="TicketToolFunctions"/> are registered as AIFunctions.
/// </summary>
public static class TicketBot
{
    public static ChatClientAgent Create(ITicketStore store)
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
                ],
            },
        });
    }
}