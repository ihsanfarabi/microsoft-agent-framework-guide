using MafDemo.Core.Domain;
using P07.ResolutionWorkflow;

namespace P07.ResolutionWorkflow.Tests;

public class ApprovalPolicyTests
{
    [Fact]
    public void Critical_needs_escalation()
        => Assert.True(ApprovalPolicy.NeedsEscalation(TicketPriority.Critical));

    [Theory]
    [InlineData(TicketPriority.Low)]
    [InlineData(TicketPriority.Normal)]
    [InlineData(TicketPriority.High)]
    public void Others_do_not(TicketPriority p) => Assert.False(ApprovalPolicy.NeedsEscalation(p));

    [Fact]
    public void Resolution_note_contains_diagnosis_and_fix()
    {
        var ctx = new TicketContext(Guid.NewGuid(), "VPN", "down", TicketPriority.High, "network", "router restart", "restart router", null);
        var note = ApprovalPolicy.ResolutionNote(ctx, new ApprovalDecision(true, "ok"));
        Assert.Contains("restart router", note);
        Assert.Contains("ok", note);
    }

    [Fact]
    public void Rejection_note_contains_note()
    {
        var decision = new ApprovalDecision(false, "too risky");
        var note = ApprovalPolicy.RejectionNote(decision);
        Assert.Contains("too risky", note);
    }
}