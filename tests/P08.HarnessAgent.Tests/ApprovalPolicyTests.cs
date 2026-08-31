using Microsoft.Extensions.AI;
using P08.HarnessAgent;

namespace P08.HarnessAgent.Tests;

public class ApprovalPolicyTests
{
    [Fact]
    public async Task Read_only_tools_auto_approve()
    {
        var call = new FunctionCallContent("callId", "list_tickets", new Dictionary<string, object?>());
        Assert.True(await ApprovalPolicy.ShouldAutoApprove(call));
    }

    [Fact]
    public async Task Close_ticket_needs_human()
    {
        var call = new FunctionCallContent("callId", "close_ticket", new Dictionary<string, object?>());
        Assert.False(await ApprovalPolicy.ShouldAutoApprove(call));
    }

    [Theory]
    [InlineData("create_ticket")]
    [InlineData("get_ticket")]
    [InlineData("add_note")]
    public async Task Remaining_read_only_tools_auto_approve(string name)
    {
        var call = new FunctionCallContent("callId", name, new Dictionary<string, object?>());
        Assert.True(await ApprovalPolicy.ShouldAutoApprove(call));
    }
}