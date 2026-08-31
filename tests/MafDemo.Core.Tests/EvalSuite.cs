using MafDemo.Core.Evals;
using MafDemo.Core.Handbook;
using P04.HandbookRag;

namespace MafDemo.Core.Tests;

/// <summary>
/// Live eval suite: 8 handbook-grounded questions against the real P04 RAG
/// stack (bge-m3 embeddings + Ollama model). Skipped unless RUN_EVALS=1 —
/// CI sets it; locally run explicitly: `RUN_EVALS=1 dotnet test --filter EvalSuite`.
/// </summary>
public class EvalSuite
{
    [Fact]
    public async Task Handbook_questions_are_grounded()
    {
        if (Environment.GetEnvironmentVariable("RUN_EVALS") != "1")
        {
            Console.WriteLine("eval: skipped (RUN_EVALS=1 not set)");
            return;
        }

        var retriever = new HandbookRetriever(new OllamaEmbedder());
        var corpus = FindCorpusDirectory();
        var chunks = corpus.GetFiles("*.md")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .SelectMany(f => HandbookChunker.Chunk(f.Name, File.ReadAllText(f.FullName)))
            .ToList();
        await retriever.BuildAsync(chunks);

        var agent = HandbookBot.Create(retriever);
        var result = await EvalRunner.RunAsync(Cases, q => agent.RunAsync(q).ContinueWith(t => t.Result.Text));

        Console.WriteLine($"eval: {result.Passed}/{result.Total} passed");
        foreach (var f in result.Failures)
            Console.WriteLine($"  FAILED: {f}");
        Assert.Equal(result.Total, result.Passed);
    }

    public static readonly EvalCase[] Cases =
    [
        // password-reset.md
        new("How do I reset my password?", "Forgot password"),
        new("How often do passwords expire?", "90"),
        new("I made 5 wrong login attempts and I'm locked out. What now?", "30"),
        // wifi-setup.md
        new("Which network should company laptops use?", "MafCorp-Secure"),
        // rma-hardware.md
        new("When must an RMA be filed after a failed hardware check?", "14 days"),
        // onboarding.md
        new("Where do I find the employee handbook?", "handbook"),
        // vpn-access.md
        new("How do I get VPN access to the network?", "VPN"),
        // helpdesk general
        new("Who do I contact when my laptop won't boot?", "IT portal"),
    ];

    private static DirectoryInfo FindCorpusDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "docs", "corpus");
            if (Directory.Exists(probe))
                return new DirectoryInfo(probe);
        }
        throw new DirectoryNotFoundException("no docs/corpus above the test binary");
    }
}