using MafDemo.AgentCommon;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P06.TriageComposition;

/// <summary>
/// Shared tool bodies for the three specialists: handbook retrieval (the same
/// <see cref="HandbookRetriever"/> P04 indexes) and ticket lookup (same
/// <see cref="ITicketStore"/> used across P02/P05). Thin on purpose — all
/// retrieval and store logic lives in the reused projects; this class only
/// shapes the strings the model sees.
///
/// The methods are named <c>*Async</c> because <see cref="AIFunctionFactory"/>
/// strips that suffix when deriving tool names; the registration in
/// <see cref="Specialists"/> pins the wire names <c>search_handbook</c> and
/// <c>get_ticket</c> explicitly anyway.
/// </summary>
public sealed class SpecialistTools(ITicketStore ticketStore, HandbookRetriever handbookRetriever)
{
    private const int TopK = 3;

    /// <summary>
    /// Embeds the query and joins the top chunks as "[doc #index]" blocks —
    /// the same shape P04's tool variant returns, so citation behavior stays
    /// comparable across projects.
    /// </summary>
    public async Task<string> SearchHandbookAsync(string query)
    {
        var hits = await handbookRetriever.SearchAsync(query, topK: TopK);
        return hits.Count == 0
            ? "(no handbook excerpts matched)"
            : string.Join("\n---\n", hits.Select(h => $"[{h.Doc} #{h.Index}]\n{h.Text}"));
    }

    /// <summary>Looks a ticket up by its GUID and renders a one-line summary
    /// (plus notes when present); missing or malformed ids return a message
    /// instead of throwing, so the model can recover in-band.</summary>
    public async Task<string> GetTicketAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return $"Invalid ticket id: {id}";
        var ticket = await ticketStore.GetAsync(guid);
        return ticket is null
            ? $"Ticket {id} not found"
            : $"{ticket.Id} | {ticket.Status} | {ticket.Priority} | {ticket.Title} | {ticket.Description}"
              + (ticket.Notes.Count == 0 ? "" : $"\nnotes: {string.Join(" / ", ticket.Notes)}");
    }
}

/// <summary>
/// The three HelpDeskHQ specialists used by the triage composition (later
/// tasks wrap these agents as tools / handoff targets). Each one gets its own
/// <see cref="ChatClientBuilder"/> pipeline with <c>UseFunctionInvocation</c>
/// (required for the client-side tool loop — P02/P04 pattern) on the shared
/// <see cref="OllamaChat"/> factory, its own instructions, and its own tool set:
/// Network and Hardware ground only with the handbook; Software additionally
/// sees <c>get_ticket</c>.
/// </summary>
public static class Specialists
{
    public static ChatClientAgent NetworkSpecialist(SpecialistTools tools) =>
        CreateCore(
            "NetworkSpecialist",
            """
            You are HelpDeskHQ's network specialist. Diagnose connectivity, Wi-Fi, VPN issues using the handbook. Answer concisely with steps.
            """,
            [SearchHandbook(tools)]);

    public static ChatClientAgent SoftwareSpecialist(SpecialistTools tools) =>
        CreateCore(
            "SoftwareSpecialist",
            """
            You are HelpDeskHQ's software specialist. Diagnose application crashes, licensing, and software install issues using the handbook, and look up ticket details with the get_ticket tool. Answer concisely with steps.
            """,
            [SearchHandbook(tools), GetTicket(tools)]);

    public static ChatClientAgent HardwareSpecialist(SpecialistTools tools) =>
        CreateCore(
            "HardwareSpecialist",
            """
            You are HelpDeskHQ's hardware specialist. Diagnose laptops, printers, and peripheral failures using the handbook; point the user at the RMA process when replacement is needed. Answer concisely with steps.
            """,
            [SearchHandbook(tools)]);

    /// <summary>Shared construction: one <see cref="IChatClient"/> per specialist
    /// (per the plan — cheap, keeps the invocation loop per agent), the repo's
    /// established <see cref="ChatClientAgent"/> wiring from P02/P04.</summary>
    private static ChatClientAgent CreateCore(string name, string instructions, IReadOnlyList<AITool> tools)
    {
        IChatClient chatClient = new ChatClientBuilder(OllamaChat.Create())
            .UseFunctionInvocation()
            .Build();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = [.. tools],
            },
        });
    }

    private static AIFunction SearchHandbook(SpecialistTools tools) =>
        AIFunctionFactory.Create(
            tools.SearchHandbookAsync,
            name: "search_handbook",
            description:
                "Search the company IT handbook for policy and troubleshooting facts. Returns cited excerpts "
                + "formatted as [doc #index] blocks.");

    private static AIFunction GetTicket(SpecialistTools tools) =>
        AIFunctionFactory.Create(
            tools.GetTicketAsync,
            name: "get_ticket",
            description:
                "Look up a support ticket by its GUID and return its status, priority, title, description and notes.");
}
