// tests/MafDemo.Core.Tests/KnowledgeScorerTests.cs
using MafDemo.Core.Handbook;

public class KnowledgeScorerTests
{
    // Resolves the repo's docs/corpus directory by walking up from the test
    // run directory (bin/Debug/.../testhost) to the repository root.
    private static string CorpusDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "corpus")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "docs", "corpus");
    }

    private static IReadOnlyList<(string Doc, string Text)> LoadCorpusChunks()
    {
        var chunks = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(CorpusDir(), "*.md").OrderBy(f => f))
            foreach (var c in HandbookChunker.Chunk(Path.GetFileName(file), File.ReadAllText(file)))
                chunks.Add((c.Doc, c.Text));
        return chunks;
    }

    [Fact]
    public void Score_prefers_token_overlap()
    {
        double hit = KnowledgeScorer.Score("password expired", "VPN password expires every 90 days.");
        double miss = KnowledgeScorer.Score("password expired", "Printer queue stuck in spooler loop.");
        Assert.True(hit > 0 && hit > miss);
    }

    [Fact]
    public void Search_ranks_known_doc_first()
    {
        var chunks = LoadCorpusChunks();
        Assert.NotEmpty(chunks);
        var top = KnowledgeScorer.Search("password expired", 1, chunks);
        var firstLine = top.Split('\n')[0];
        Assert.StartsWith("password-reset.md |", firstLine);
        Assert.Contains("password", firstLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_or_whitespace_query_never_throws()
    {
        Assert.Equal(0, KnowledgeScorer.Score("", "any text"));
        Assert.Equal(0, KnowledgeScorer.Score("   ", "any text"));
        Assert.Equal(0, KnowledgeScorer.Score("???", "any text"));

        var chunks = new List<(string Doc, string Text)> { ("a.md", "some text") };
        Assert.Equal("", KnowledgeScorer.Search("", 5, chunks));
        Assert.Equal("", KnowledgeScorer.Search("   ", 5, chunks));
        Assert.Equal("", KnowledgeScorer.Search("password", 0, chunks));
        Assert.Equal("", KnowledgeScorer.Search("password", 5, new List<(string, string)>()));
    }
}
