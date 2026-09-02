// tests/MafDemo.Core.Tests/HandbookRetrieverTests.cs
using MafDemo.Core.Handbook;

public class KeywordEmbedder : IEmbedder   // deterministic: word order vector
{
    public Task<float[]> EmbedAsync(string text)
    {
        var v = new float[64];
        foreach (var word in text.ToLower().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ch in word) v[ch % 64] += 1f;
        return Task.FromResult(v);
    }
}

/// <summary>Flips dimension between calls — builds 64-dim chunks, then
/// answers queries in 32 dims: the OLLAMA_EMBEDDING_MODEL switch, in miniature.</summary>
public class SwitchingEmbedder : IEmbedder
{
    public bool UseSmall { get; set; }
    public Task<float[]> EmbedAsync(string text)
    {
        var v = new float[UseSmall ? 32 : 64];
        v[text.Length % (UseSmall ? 32 : 64)] = 1f;
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
        Assert.Equal(2, (await r.SearchAsync("backups", topK: 2, minScore: 0f)).Count);
    }

    [Fact]
    public async Task BuildAsync_called_twice_does_not_duplicate_entries()
    {
        var r = new HandbookRetriever(new KeywordEmbedder());
        await r.BuildAsync(Chunks);
        await r.BuildAsync(Chunks);        // rebuild — same corpus, fresh process pattern

        // topK above the corpus size, floor 0: the whole index. Without the
        // clear in BuildAsync this would return 6 (each chunk twice).
        var hits = await r.SearchAsync("how many vacation days do I get?", topK: 10, minScore: 0f);
        Assert.Equal(Chunks.Length, hits.Count);
        Assert.Equal(Chunks.Length, hits.Select(h => (h.Doc, h.Index)).Distinct().Count());
    }

    [Fact]
    public async Task SearchAsync_dimension_mismatch_throws_clear_error()
    {
        // Built 64-dim, queried 32-dim — the OLLAMA_EMBEDDING_MODEL switch class.
        var embedder = new SwitchingEmbedder();
        var r = new HandbookRetriever(embedder);
        await r.BuildAsync(Chunks);                  // UseSmall = false: 64-dim chunks

        embedder.UseSmall = true;                    // queries now come back 32-dim
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => r.SearchAsync("vpn"));
        Assert.Contains("dimension", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_unrelated_query_returns_nothing()
    {
        var r = new HandbookRetriever(new KeywordEmbedder());
        await r.BuildAsync(Chunks);
        // A query sharing no characters with any chunk scores 0 everywhere —
        // below the floor, so the caller's "no handbook match" branch is reachable.
        var hits = await r.SearchAsync("zzz?? qqq?? www??");
        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchAsync_zero_floor_returns_topK_regardless_of_score()
    {
        var r = new HandbookRetriever(new KeywordEmbedder());
        await r.BuildAsync(Chunks);
        Assert.Equal(2, (await r.SearchAsync("backups", topK: 2, minScore: 0f)).Count);
    }
}
