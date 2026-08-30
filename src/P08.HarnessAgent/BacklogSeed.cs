using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

namespace P08.HarnessAgent;

/// <summary>
/// Seeds the demo IT helpdesk backlog — the work queue the P08 harness agent
/// will pick tickets from. Idempotent: re-running against an already-seeded
/// store is a no-op, so a crashed run can simply be started again.
/// </summary>
public static class BacklogSeed
{
    private static readonly (string Title, string Desc, TicketPriority P)[] Backlog =
    [
        ("VPN fails from home network", "Cannot connect since router change", TicketPriority.High),
        ("Outlook calendar not syncing", "Meetings missing on phone", TicketPriority.Normal),
        ("Laptop fan running loud", "Overheating during builds", TicketPriority.Normal),
        ("Cannot install Python 3.12", "UAC blocks pip", TicketPriority.Low),
        ("Printer offline in pod 4", "Queue stuck", TicketPriority.Low),
    ];

    public static async Task RunAsync(ITicketStore store)
    {
        if ((await store.ListAsync()).Count >= Backlog.Length) return;
        foreach (var (title, desc, p) in Backlog)
            await store.CreateAsync(title, desc, p);
    }
}