using System.Reflection;
using System.Text.Json;
using MafDemo.AgentCommon;
using MafDemo.Core.Domain;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using P04.HandbookRag;
using P07.ResolutionWorkflow;

// Start OTLP tracing first (same Aspire-dashboard wiring as P05-P08).
using var telemetry = Telemetry.StartOtlp("P09.DurableHost");

// Same corpus indexing + file-backed ticket store as P07 — durability of the
// ticket states must survive the kill-and-resume restart too.
var retriever = new HandbookRetriever(new OllamaEmbedder());
var chunks = FindCorpusDirectory()
    .GetFiles("*.md")
    .OrderBy(f => f.Name, StringComparer.Ordinal)
    .SelectMany(f => HandbookChunker.Chunk(f.Name, File.ReadAllText(f.FullName)))
    .ToList();
await retriever.BuildAsync(chunks);
Console.WriteLine($"indexed {chunks.Count} handbook chunks");

ITicketStore store = new FileTicketStore("p09-tickets.json");

const string RunInfoFile = "p09-runinfo.json";

string cs = Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")
    ?? "Endpoint=http://localhost:8080;TaskHub=default;Authentication=None";

var workflow = ResolutionWorkflowFacts.Build(store, retriever);

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.ConfigureDurableWorkflows(
            workflowOptions => workflowOptions.AddWorkflow(workflow),
            workerBuilder: b => b.UseDurableTaskScheduler(cs),
            clientBuilder: b => b.UseDurableTaskScheduler(cs));
    })
    .Build();
await host.StartAsync();
Console.WriteLine($"[host] started, scheduler: {cs}");

// The P07 graph ends in a RequestPort (FixApproval HITL) — stream the run and
// answer pending requests from the console. "k" at a prompt kills the host
// WITHOUT answering: the paused orchestration lives on in the scheduler, and
// `dotnet run -- resume` re-attaches to the same run.
IWorkflowClient client = host.Services.GetRequiredService<IWorkflowClient>();

if (args.FirstOrDefault() == "resume")
{
    if (!File.Exists(RunInfoFile))
    {
        Console.WriteLine("no run info found — nothing to resume");
        return 1;
    }
    var record = JsonSerializer.Deserialize<RunRecord>(await File.ReadAllTextAsync(RunInfoFile))
        ?? throw new InvalidOperationException("corrupt run info file");
    await DriveResumeAsync(host.Services, workflow, record.RunId, RunInfoFile);
}
else
{
    var ticket = await store.CreateAsync(
        "Wi-Fi drops every 5 minutes",
        "Wireless connection to the office network reconnects repeatedly throughout the day",
        TicketPriority.High);
    Console.WriteLine($"created ticket {ticket.Id}");
    var ctx = new TicketContext(ticket.Id, ticket.Title, ticket.Description, ticket.Priority,
        Triage: "", Diagnosis: "", ProposedFix: null, OperatorNote: null);

    IStreamingWorkflowRun run = await client.StreamAsync(workflow, ctx, runId: Guid.NewGuid().ToString());
    await File.WriteAllTextAsync(RunInfoFile, JsonSerializer.Serialize(new RunRecord(run.RunId)));
    Console.WriteLine($"[host] run {run.RunId} started");

    int exit = await DriveEventsAsync(run, RunInfoFile);
    await host.StopAsync();
    return exit;
}

await host.StopAsync();
return 0;

// ---- Event loop shared by fresh and resumed runs. Answering "k" at the
// prompt exits without answering — the durable orchestration pauses at the
// RequestPort regardless of host lifetime.
static async Task<int> DriveEventsAsync(IStreamingWorkflowRun run, string runInfoFile)
{
    await foreach (WorkflowEvent evt in run.WatchStreamAsync())
    {
        switch (evt)
        {
            case DurableWorkflowWaitingForInputEvent reqEvt:
                // The durable input event carries the JSON-serialized request
                // (RequestInfoEvent's typed accessor doesn't exist here).
                var request = JsonSerializer.Deserialize<FixApprovalRequest>(reqEvt.Input);
                if (request is null)
                {
                    Console.Error.WriteLine($"[unexpected request] {reqEvt.RequestPort.Id}: {reqEvt.Input}");
                    continue;
                }
                Console.WriteLine($"PROPOSED FIX: {request.ProposedFix}");
                Console.Write("approve? (y/n/k to kill host, workflow survives): ");
                var line = Console.ReadLine() ?? "n";
                var choice = line.Length > 0 ? char.ToLowerInvariant(line[0]).ToString() : "n";
                if (choice == "k")
                {
                    Console.WriteLine("[killed] stopping host mid-approval; the orchestration stays paused in the scheduler; restart with `dotnet run -- resume`");
                    return 137;
                }
                var note = line.Length > 1 ? line[1..].Trim() : "";
                await run.SendResponseAsync(reqEvt, new ApprovalDecision(choice == "y", note));
                break;

            case DurableWorkflowCompletedEvent done:
                Console.WriteLine($"[done] {done.Result}");
                return 0;

            case DurableWorkflowFailedEvent failed:
                Console.Error.WriteLine($"[workflow failed] {failed.ErrorMessage}");
                return 1;

            case WorkflowErrorEvent errEvt:
                Console.Error.WriteLine($"[workflow error] {errEvt.Exception}");
                break;
        }
    }
    return 0;
}

// ---- Resume: re-attach the event stream to the run the scheduler still holds.
// IWorkflowClient only schedules new instances, and the durable run handle's
// constructor is internal — WatchStreamAsync is pure polling over instance
// custom status, so rebuilding the handle over the saved RunId is all a
// re-attach needs (documented divergence in NOTES.md).
static async Task<int> DriveResumeAsync(IServiceProvider services, Workflow workflow, string runId, string runInfoFile)
{
    var client = services.GetRequiredService<DurableTaskClient>();
    var status = await client.GetInstanceAsync(runId, false);
    if (status is null)
    {
        Console.WriteLine($"run {runId} not found in the task hub");
        return 1;
    }
    Console.WriteLine($"re-attached to run {runId} (status: {status.RuntimeStatus})");

    IStreamingWorkflowRun run = (IStreamingWorkflowRun)Activator.CreateInstance(
        typeof(IWorkflowClient).Assembly.GetType(
            "Microsoft.Agents.AI.DurableTask.Workflows.DurableStreamingWorkflowRun", throwOnError: true)!,
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [client, runId, workflow],
        culture: null)!;

    return await DriveEventsAsync(run, runInfoFile);
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

internal sealed record RunRecord(string RunId);