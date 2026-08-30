using MafDemo.Core.Guardrails;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P05.GuardrailMiddleware.Middleware;

/// <summary>
/// Closed-generic alias for the run-middleware delegate accepted by
/// <see cref="AIAgentBuilder.Use"/> — verified against the shipped
/// Microsoft.Agents.AI 1.19.0 package: there is no named
/// <c>AgentRunDelegate</c> type in 1.19.0, the overload takes the raw
/// <see cref="Func{T1,T2,T3,T4,T5,TResult}"/> shape, and its session and
/// options parameters are nullable (a non-nullable alias compiles but
/// warns CS8620).
/// </summary>
using RunMiddlewareFunc =
    Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent,
        CancellationToken, Task<AgentResponse>>;

/// <summary>
/// Run middlewares for the guardrail demo, each returned as the anonymous
/// run delegate shape <see cref="AIAgentBuilder.Use"/> expects. Only the
/// non-streaming run func is provided; per the 1.19.0 package docs the
/// builder uses it for <c>RunAsync</c> and for <c>RunStreamingAsync</c>
/// (with limited streaming over the batch output), so a single func is
/// sufficient for both call paths.
/// </summary>
public static class RunMiddlewares
{
    /// <summary>
    /// Logs the message counts entering and leaving the inner agent run.
    /// Materializes the incoming sequence first: an <see cref="IEnumerable{T}"/>
    /// may be one-shot, and both <see cref="Linq.Enumerable.Count{TSource}"/>
    /// and the inner run enumerate it.
    /// </summary>
    public static RunMiddlewareFunc Logging() =>
        async (messages, session, options, innerAgent, ct) =>
        {
            var input = messages.ToList();
            Console.WriteLine($"[log] run start: {input.Count} message(s)");
            var response = await innerAgent.RunAsync(input, session, options, ct);
            Console.WriteLine($"[log] run end: {response.Messages.Count} message(s)");
            return response;
        };

    /// <summary>
    /// Strips PII (employee IDs, email addresses) from user input before it
    /// reaches the inner agent — the model never sees the raw values — and
    /// from assistant output text before the response is returned
    /// (<see cref="TextContent.Text"/> is settable, so the pass is a clean
    /// in-place rewrite of text items on the response's assistant messages;
    /// function-call contents are left untouched).
    /// </summary>
    public static RunMiddlewareFunc Redaction() =>
        async (messages, session, options, innerAgent, ct) =>
        {
            var redactedInput = messages.Select(RedactUserMessage).ToList();
            var response = await innerAgent.RunAsync(redactedInput, session, options, ct);

            foreach (var message in response.Messages)
            {
                if (message.Role != ChatRole.Assistant)
                    continue;
                foreach (var content in message.Contents.OfType<TextContent>())
                {
                    var redacted = PiiRedactor.Redact(content.Text);
                    if (redacted != content.Text)
                    {
                        Console.WriteLine($"[redact] assistant output: {content.Text} -> {redacted}");
                        content.Text = redacted;
                    }
                }
            }

            return response;
        };

    /// <summary>
    /// Clones a user message with its text content redacted; non-user
    /// messages pass through by reference. Cloning (rather than mutating)
    /// keeps the caller's message objects unmodified. All user messages are
    /// redacted, not just the last one, so session-supplied history is
    /// covered too. Echoes each rewrite so the guardrail's effect is
    /// visible on the console (and in the captured transcript).
    /// </summary>
    private static ChatMessage RedactUserMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.User)
            return message;

        var clone = message.Clone();
        clone.Contents =
        [
            .. clone.Contents.Select(c => c is TextContent text
                ? RedactText(text)
                : c),
        ];
        return clone;
    }

    private static TextContent RedactText(TextContent text)
    {
        var redacted = PiiRedactor.Redact(text.Text);
        if (redacted != text.Text)
            Console.WriteLine($"[redact] user input: {text.Text} -> {redacted}");
        return new TextContent(redacted);
    }
}
