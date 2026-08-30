using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using P02.TicketTools;

namespace P05.GuardrailMiddleware;

/// <summary>
/// Thin alias over P02's <see cref="TicketBot"/>: keeps the plan's
/// <c>TicketAgent.Create(store)</c> name so Program.cs reads naturally,
/// with zero duplicated agent logic — the underlying agent (tools,
/// <c>UseFunctionInvocation</c> loop, OTel-wrapped Ollama client) is all
/// in <see cref="TicketBot.Create"/> and is consumed here as-is.
/// </summary>
public static class TicketAgent
{
    /// <summary>HelpDeskHQ ticket agent with the four ticket tools.</summary>
    public static AIAgent Create(ITicketStore store) => TicketBot.Create(store);
}
