using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

namespace P08.HarnessAgent;

/// <summary>
/// Seeds the demo IT helpdesk backlog — the work queue the P08 harness agent
/// will pick tickets from. Idempotent per title: only backlog titles not
/// already present in the store are created, so re-running after a crash
/// mid-seed (e.g. after 2 of the 5 creates) resumes to exactly the full
/// backlog with no duplicates, and a re-run against a fully seeded store is
/// a no-op.
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
        var present = (await store.ListAsync()).Select(t => t.Title).ToHashSet();
        foreach (var (title, desc, p) in Backlog)
            if (!present.Contains(title))
                await store.CreateAsync(title, desc, p);
    }
}
