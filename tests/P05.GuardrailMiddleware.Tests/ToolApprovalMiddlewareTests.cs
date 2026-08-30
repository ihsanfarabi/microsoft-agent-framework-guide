using Microsoft.Extensions.AI;
using P05.GuardrailMiddleware.Middleware;

public class ToolApprovalMiddlewareTests
{
    private const string NextResult = "next-ran";

    /// <summary>
    /// Builds a function-invocation context the way FunctionInvokingChatClient
    /// would hand one to the middleware: an AIFunction whose name matches the
    /// registered tool name (P02 registers <c>UpdateTicketStatusAsync</c> with
    /// no override; AIFunctionFactory's default naming strips the Async
    /// suffix, so the tool is <c>UpdateTicketStatus</c> — verified live) plus
    /// the call arguments. The context has a public parameterless constructor and
    /// settable <see cref="FunctionInvocationContext.Function"/> /
    /// <see cref="FunctionInvocationContext.Arguments"/> (verified against
    /// the shipped Microsoft.Extensions.AI 10.9.0 XML).
    /// </summary>
    private static FunctionInvocationContext MakeContext(string functionName, string? status = null)
    {
        Func<string, string, string> updateStatus = (id, status) => "status updated";
        return new FunctionInvocationContext
        {
            Function = AIFunctionFactory.Create(
                updateStatus, new AIFunctionFactoryOptions { Name = functionName }),
            Arguments = status is null
                ? []
                : new AIFunctionArguments { ["id"] = Guid.NewGuid().ToString(), ["status"] = status },
        };
    }

    [Fact]
    public async Task Destructive_call_with_prompt_false_returns_rejection_without_invoking_next()
    {
        var nextInvoked = false;
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next =
            (_, _) => { nextInvoked = true; return ValueTask.FromResult<object?>(NextResult); };

        var middleware = ToolApprovalMiddleware.Create(prompt: _ => false);
        var context = MakeContext("UpdateTicketStatus", "Closed");

        var result = await middleware(null!, context, next, CancellationToken.None);

        Assert.False(nextInvoked);
        var text = Assert.IsType<string>(result);
        Assert.Contains("Rejected by operator approval", text);
    }

    [Fact]
    public async Task Destructive_call_with_prompt_true_invokes_next()
    {
        var nextInvoked = false;
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next =
            (_, _) => { nextInvoked = true; return ValueTask.FromResult<object?>(NextResult); };

        var middleware = ToolApprovalMiddleware.Create(prompt: _ => true);
        var context = MakeContext("UpdateTicketStatus", "Closed");

        var result = await middleware(null!, context, next, CancellationToken.None);

        Assert.True(nextInvoked);
        Assert.Equal(NextResult, result);
    }

    [Theory]
    [InlineData("UpdateTicketStatus", "InProgress")] // same tool, non-Closed status
    [InlineData("ListTickets", null)]                // different tool, no status arg
    public async Task Non_destructive_call_passes_through_without_consulting_prompt(
        string functionName, string? status)
    {
        var nextInvoked = false;
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next =
            (_, _) => { nextInvoked = true; return ValueTask.FromResult<object?>(NextResult); };

        // prompt:false would veto anything it is asked about — the pass-through
        // only proves out if the prompt is never consulted for this call.
        var middleware = ToolApprovalMiddleware.Create(prompt: _ => false);
        var context = MakeContext(functionName, status);

        var result = await middleware(null!, context, next, CancellationToken.None);

        Assert.True(nextInvoked);
        Assert.Equal(NextResult, result);
    }
}
