// tests/MafDemo.Core.Tests/HandbookRetrieverTests.cs
using MafDemo.Core.Handbook;

public class KeywordEmbedder : IEmbedder   // deterministic: word order vector
{
    public Task<float[]> EmbedAsync(string text)
    {
        var v = new float[64];
        foreach (var word in text.ToLower().Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ch in word) v[ch % 64] += 1f;
        return Task.FromResult(v);
    }
}

public class HandbookRetrieverTests
{
    private static readonly HandbookChunk[] Chunks =
    [
        new("onboarding.md", 0, "Employees get 25 vacation days per year."),
        new("vpn-policy.md", 0, "VPN reconnects must use MFA every 8 hours."),
        new("backup-policy.md", 0, "Backups run nightly at 2am to the Franklin region."),
    ];

    [Fact]
    public async Task Search_returns_relevant_chunk_first()
    {
        var r = new HandbookRetriever(new KeywordEmbedder());
        await r.BuildAsync(Chunks);
        var hits = await r.SearchAsync("how many vacation days do I get?");
        Assert.Equal("onboarding.md", hits[0].Doc);
    }

    [Fact]
    public async Task Search_respects_topK()
    {
        var r = new HandbookRetriever(new KeywordEmbedder());
        await r.BuildAsync(Chunks);
        Assert.Equal(2, (await r.SearchAsync("backups", topK: 2)).Count);
    }
}
