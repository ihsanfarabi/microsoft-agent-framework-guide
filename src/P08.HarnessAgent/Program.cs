using System.Text.Json;
using MafDemo.AgentCommon;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using P08.HarnessAgent;

// One work/ area next to the binary: the harness agent's file-access store
// (Handbook copies land in work/handbook/ here) and, in the same tree, the
// file-backed ticket store the ticket tools mutate. Runtime state, git-ignored.
var workRoot = Path.Combine(AppContext.BaseDirectory, "work");
Directory.CreateDirectory(workRoot);
CopyHandbook(workRoot);

var store = new FileTicketStore(Path.Combine(workRoot, "tickets.json"));
await BacklogSeed.RunAsync(store);

// Console logging on the harness internals: the batch is a long autonomous
// run, and the MAF harness logs one line per model turn through the logger
// factory accepted by AsHarnessAgent — without it the run is silent for
// minutes (see the task report for how this logging also drove diagnosis).
using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o => o.SingleLine = true));

// Ruling-3 resolution: NO UseFunctionInvocation wrapper — the harness wires
// its own function-invocation layer around the client (verified in the MAF
// HarnessAgent source and at runtime; see the task report).
var agent = HarnessFacts.Build(OllamaChat.Create(), TicketTools.All(new TicketTools(store)), loggerFactory);

var session = await agent.CreateSessionAsync();
// Canonical harness drive loop (MAF get-started doc): the multi-step run
// progresses as streaming updates are enumerated. close_ticket is an
// ApprovalRequiredAIFunction, so when the model asks to close, the harness
// ends the run surfacing a ToolApprovalRequestContent — answer it on the
// console (y/n/a) and resume the same session; "a" records a standing
// rule, after which later closes auto-pass with no prompt.
ToolApprovalRequestContent? approvalRequest = null;
await foreach (var update in agent.RunStreamingAsync("Work the ticket backlog.", session))
{
    Console.Write(update);
    approvalRequest ??= update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault();
}

while (approvalRequest is not null)
{
    var response = PromptApproval(approvalRequest);
    approvalRequest = null;
    await foreach (var update in agent.RunStreamingAsync(new ChatMessage(ChatRole.User, [response]), session))
    {
        Console.Write(update);
        approvalRequest ??= update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault();
    }
}
Console.WriteLine();

// Post-run evidence: what the batch actually did to the store.
Console.WriteLine("\n---- final ticket state ----");
foreach (var t in await store.ListAsync())
    Console.WriteLine($"{t.Id} | {t.Status} | {t.Priority} | {t.Title}"
        + (t.Notes.Count == 0 ? "" : $" | {t.Notes.Count} note(s)"));

/// <summary>Prints the approval request (tool name + arguments) and reads the
/// operator's answer: <c>y</c> approves this one call, <c>a</c> approves it and
/// records a standing "always approve this tool" rule in the session state
/// (the harness's own mechanism, so later closes auto-pass), anything else
/// declines and the model is told to leave the ticket open.</summary>
static AIContent PromptApproval(ToolApprovalRequestContent request)
{
    // The request's ToolCall is the model's original call — at runtime a
    // FunctionCallContent carrying the wire name and arguments (10.9's
    // ToolCallContent base type only exposes the CallId).
    var call = request.ToolCall as FunctionCallContent;
    Console.WriteLine($"\n[approval] {call?.Name ?? "unknown tool"} {JsonSerializer.Serialize(call?.Arguments)}");
    Console.Write("[approval] allow? (y = once, a = always for this tool, n = decline): ");
    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
    return answer switch
    {
        "a" => request.CreateAlwaysApproveToolResponse("operator: always approve this tool"),
        "y" => request.CreateResponse(approved: true, reason: "operator approved"),
        _ => request.CreateResponse(approved: false, reason: "operator declined"),
    };
}

/// <summary>Copies the shared handbook corpus (docs/corpus, located by walking
/// up from the binary — P07's corpus finder pattern) into the file-access
/// store's handbook/ folder, so the agent's file_access_ls/grep/read tools
/// have real policy docs to consult during the batch.</summary>
static void CopyHandbook(string workRoot)
{
    var corpus = FindCorpusDirectory();
    var target = Path.Combine(workRoot, "handbook");
    Directory.CreateDirectory(target);
    foreach (var file in corpus.GetFiles("*.md"))
        File.Copy(file.FullName, Path.Combine(target, file.Name), overwrite: true);
    Console.WriteLine($"copied {corpus.GetFiles("*.md").Length} handbook docs to {target}");
}

static DirectoryInfo FindCorpusDirectory()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var probe = Path.Combine(dir.FullName, "docs", "corpus");
        if (Directory.Exists(probe))
            return new DirectoryInfo(probe);
    }

    throw new DirectoryNotFoundException(
        $"could not find docs/corpus in any parent of {AppContext.BaseDirectory}");
}
