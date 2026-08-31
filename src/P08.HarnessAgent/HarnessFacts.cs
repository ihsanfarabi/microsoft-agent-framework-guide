using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace P08.HarnessAgent;

/// <summary>
/// Constructs the P08 harness agent: the shared <c>OllamaChat</c> client
/// wrapped in the MAF agent harness — todo planning, per-session chat history,
/// file memory and tool approval come from the harness itself; this project
/// only supplies the ticket tools and a file-access store rooted at the run's
/// <c>work/</c> directory (handbook copies land in <c>work/handbook/</c> at
/// startup, so the <c>file_access_*</c> tools have something to consult).
/// </summary>
public static class HarnessFacts
{
    /// <summary>Builds the overnight harness agent over the given client.
    /// The instructions drive the batch loop: track each backlog ticket in the
    /// todo list, consult the handbook, add a resolution note, request
    /// approval to close.</summary>
    public static AIAgent Build(IChatClient client, AITool[] tools, ILoggerFactory? loggerFactory = null)
    {
        // FileAccessStore/FileSystemAgentFileStore are [Experimental] in MAF
        // 1.19.0 — the analyzer (MAAI001) escalates them to errors by default.
        // P08's whole point is exercising the harness file-access surface, so
        // the diagnostic is suppressed for exactly this construction site.
#pragma warning disable MAAI001 // experimental API, subject to change
        return client.AsHarnessAgent(new HarnessAgentOptions
        {
            FileAccessStore = new FileSystemAgentFileStore(
                Path.Combine(AppContext.BaseDirectory, "work")),
            // The overnight batch is unattended, so it starts in "execute" mode.
            // The default initial mode is "plan" — the first of the built-in
            // plan/execute modes — in which the model narrates a plan without
            // calling any tools, and the run ends after that single turn with
            // the whole backlog untouched (observed in the first runs).
            AgentModeProviderOptions = new AgentModeProviderOptions
            {
                DefaultMode = "execute",
            },
            // File-access tools are ApprovalRequiredAIFunction by default; an
            // unattended batch has nobody to answer the approval prompt, so the
            // first file_access_* call stalled the whole run (the model's other
            // tool calls in the same turn were never invoked either). The MAF
            // read-only auto-approval rule (ls/grep/read) keeps writes gated —
            // the overnight agent only ever needs to consult the handbook.
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [FileAccessProvider.ReadOnlyToolsAutoApprovalRule],
            },
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are HelpDeskHQ's overnight agent. For each ticket in the backlog:
                    1) track it in your todo list, 2) consult handbook docs in work/handbook/,
                    3) add a resolution note to the ticket, 4) request approval to close it.
                    Finish all tickets before reporting a summary.
                    """,
                Tools = tools,
            },
        }, loggerFactory);
#pragma warning restore MAAI001
    }
}
