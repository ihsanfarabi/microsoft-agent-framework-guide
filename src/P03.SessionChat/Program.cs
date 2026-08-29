using MafDemo.AgentCommon;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using P02.TicketTools;

// Start OTel tracing first so the provider is listening before any model call.
// Disposed on exit, which flushes the spans to the console exporter.
using var telemetry = Telemetry.Start("P03.SessionChat");

// Runtime state in the working directory (gitignored). Carried note: a corrupt
// tickets.json throws JsonException from the store ctor — demo-acceptable.
var store = new FileTicketStore("tickets.json");
var agent = TicketBot.Create(store);

// The session carries the conversation in-process: every RunAsync turn below
// appends to it, so later turns see earlier ones without restating context.
AgentSession session = await agent.CreateSessionAsync();
var sessionId = Guid.NewGuid().ToString("N")[..8];

Console.WriteLine($"session {sessionId}");
Console.WriteLine("commands: /new /list /switch <id> /quit");
while (true)
{
    Console.Write("you> ");
    var text = Console.ReadLine()?.Trim();
    // ReadLine returns null at end of input (piped stdin), which exits cleanly.
    if (text is null or "" or "/quit")
        break;

    if (text == "/new")
    {
        session = await agent.CreateSessionAsync();
        sessionId = Guid.NewGuid().ToString("N")[..8];
        Console.WriteLine($"new session {sessionId}");
        continue;
    }

    if (text == "/list")
    {
        // threads/ is Task 3's persistence directory; it may not exist yet.
        var files = Directory.Exists("threads")
            ? Directory.GetFiles("threads").Select(Path.GetFileNameWithoutExtension).ToList()
            : [];
        Console.WriteLine(files.Count == 0 ? "(none)" : string.Join("\n", files));
        continue;
    }

    if (text.StartsWith("/switch ", StringComparison.Ordinal))
    {
        Console.WriteLine("not implemented until Task 3");
        continue;
    }

    var response = await agent.RunAsync(text, session);
    Console.WriteLine($"bot> {response.Text}");
}
