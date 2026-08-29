// tests/MafDemo.Core.Tests/HandbookChunkerTests.cs
using MafDemo.Core.Handbook;

public class HandbookChunkerTests
{
    [Fact]
    public void Empty_doc_yields_no_chunks()
        => Assert.Empty(HandbookChunker.Chunk("empty.md", ""));

    [Fact]
    public void Oversized_doc_splits_at_max_chars()
    {
        var text = string.Join("\n", Enumerable.Repeat("Sentence about VPN policy. VPN is mandatory.", 40)); // ~2000 chars
        var chunks = HandbookChunker.Chunk("vpn-policy.md", text, maxChars: 500);
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 500));
    }

    [Fact]
    public void Chunks_carry_doc_name_and_sequence()
    {
        var chunks = HandbookChunker.Chunk("onboarding.md", "First para.\n\nSecond para.");
        Assert.Equal("onboarding.md", chunks[0].Doc);
        Assert.Equal(0, chunks[0].Index);
    }
}
