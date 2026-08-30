using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P05.GuardrailMiddleware.Middleware;

/// <summary>
/// Closed-generic alias for the function-invocation delegate accepted by
/// <see cref="FunctionInvocationDelegatingAgentBuilderExtensions.Use"/> —
/// verified against the shipped Microsoft.Agents.AI 1.19.0 package XML
/// (member <c>M:...FunctionInvocationDelegatingAgentBuilderExtensions.Use</c>):
/// it is an <see langword="this AIAgentBuilder"/> extension (not an instance
/// method on the builder itself), and the delegate shape is
/// <see cref="AIAgent"/> × <see cref="FunctionInvocationContext"/> × next ×
/// <see cref="CancellationToken"/> → <see cref="ValueTask{TResult}"/>. The
/// 1.19.0 remarks also require a <see cref="FunctionInvokingChatClient"/> in
/// the inner agent's chat pipeline — P02's <see cref="P02.TicketTools.TicketBot"/>
/// supplies it via <c>UseFunctionInvocation()</c>.
/// </summary>
using FunctionMiddlewareFunc =
    Func<AIAgent, FunctionInvocationContext,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
        CancellationToken, ValueTask<object?>>;

/// <summary>
/// Tool-approval guardrail: intercepts the agent's function invocations and
/// requires explicit operator approval before the destructive one — closing
/// a ticket via the registered <c>UpdateTicketStatus</c> tool — is allowed to
/// run. Any other
/// call (other tools, or the same tool with a non-Closed status) passes
/// straight through. The prompt delegate is injectable so tests can approve
/// or reject without a console.
/// </summary>
public static class ToolApprovalMiddleware
{
    /// <summary>
    /// The registered name of P02's update-status tool as it actually reaches
    /// the middleware. P02 registers the <c>UpdateTicketStatusAsync</c> method
    /// with no name override, but <see cref="AIFunctionFactory"/>'s default
    /// naming strips the trailing Async suffix, so the AIFunction (and the
    /// tool definition the model sees) is <c>UpdateTicketStatus</c>. Verified
    /// live: a first run gating on "UpdateTicketStatusAsync" never fired and
    /// the ticket closed unapproved — the middleware's own [tool] log showed
    /// the real name. The plan's snake-case "update_ticket_status" maps here.
    /// </summary>
    private const string UpdateStatusFunctionName = "UpdateTicketStatus";

    private const string ClosedStatus = "Closed";

    /// <param name="prompt">
    /// Decides approval for a call; receives the question shown to the
    /// operator. When null, the middleware prompts on the console
    /// (<c>y</c> approves, anything else rejects).
    /// </param>
    public static FunctionMiddlewareFunc Create(Func<string, bool>? prompt = null) =>
        async (agent, context, next, ct) =>
        {
            Console.WriteLine($"[tool] {context.Function.Name}");

            if (!ClosesTicket(context))
                return await next(context, ct);

            var id = context.Arguments.TryGetValue("id", out var ticketId) ? ticketId?.ToString() : "?";
            var question = $"[approval] close ticket {id}? (y/n): ";
            Console.Write(question);

            var approved = prompt is not null
                ? prompt(question)
                : string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

            if (approved)
                return await next(context, ct);

            Console.WriteLine($"[approval] declined — ticket {id} stays open");
            return "Rejected by operator approval. Do not close the ticket.";
        };

    /// <summary>
    /// True only for the update-status tool called with status Closed (the
    /// plan's snake-case <c>update_ticket_status</c> maps to the registered
    /// <c>UpdateTicketStatus</c>). Status comparison is ordinal
    /// case-insensitive so a model sending "closed" is still gated.
    /// Note: <see cref="AIFunctionArguments"/> is an
    /// <see cref="System.Collections.Generic.IDictionary{TKey,TValue}"/> —
    /// it has no <c>GetValue</c> helper (brief-sketch drift), so argument
    /// access uses <c>TryGetValue</c>.
    /// </summary>
    private static bool ClosesTicket(FunctionInvocationContext context) =>
        context.Function.Name == UpdateStatusFunctionName
        && context.Arguments.TryGetValue("status", out var status)
        && string.Equals(status?.ToString(), ClosedStatus, StringComparison.OrdinalIgnoreCase);
}
