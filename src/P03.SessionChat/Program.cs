using MafDemo.AgentCommon;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using P02.TicketTools;
using P03.SessionChat;

// Start OTel tracing first so the provider is listening before any model call.
// Disposed on exit, which flushes the spans to the console exporter.
using var telemetry = Telemetry.Start("P03.SessionChat");

// Runtime state in the working directory (gitignored). A corrupt tickets.json
// is preserved as tickets.json.corrupt and the store starts empty.
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
            ? Directory.GetFiles("threads", "*.json").Select(Path.GetFileNameWithoutExtension).ToList()
            : [];
        Console.WriteLine(files.Count == 0 ? "(none)" : string.Join("\n", files));
        continue;
    }

    if (text.StartsWith("/switch", StringComparison.Ordinal))
    {
        // Bare "/switch" (no id) is a usage error, not a prompt for the agent.
        var id = text["/switch".Length..].Trim();
        if (id.Length == 0)
        {
            Console.WriteLine("usage: /switch <id>  (ids: /list)");
        }
        else if (!IsValidSessionId(id))
        {
            // Session ids are 8-char lowercase hex; anything else would let an
            // unsanitized id reach the file system (e.g. "../x" path traversal).
            Console.WriteLine("unknown session id");
        }
        else if (!File.Exists(Path.Combine("threads", $"{id}.json")))
        {
            Console.WriteLine($"no saved session {id}  (ids: /list)");
        }
        else
        {
            session = await SessionPersistence.LoadAsync(agent, id);
            sessionId = id;
            Console.WriteLine($"switched to session {sessionId}");
        }
        continue;
    }

    var response = await agent.RunAsync(text, session);
    Console.WriteLine($"bot> {response.Text}");
    // Persist after every turn so the conversation survives a process restart
    // (and so /list reflects what /switch can actually restore). A failed save
    // (disk full, unwritable threads/) must not kill the REPL — warn and continue.
    try
    {
        await SessionPersistence.SaveAsync(agent, session, sessionId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"warn: could not save session ({ex.Message})");
    }
}

// Session ids we mint are Guid.NewGuid().ToString("N")[..8]: exactly 8 chars
// of [0-9a-f]. Enforcing that shape keeps /switch ids from escaping threads/.
static bool IsValidSessionId(string id) =>
    id.Length == 8 && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
