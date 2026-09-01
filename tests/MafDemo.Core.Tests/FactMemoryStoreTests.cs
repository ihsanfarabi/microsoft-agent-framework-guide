using MafDemo.Core.Memory;
using Microsoft.Extensions.AI;

namespace MafDemo.Core.Tests;

public class FactMemoryStoreTests
{
    // Deterministic offline geometry: each known string maps to a fixed
    // unit-ish vector. Orthogonal bases make distinct facts score cosine 0
    // against each other; reusing a vector for a query and its target fact
    // gives cosine 1, which stands in for "near-duplicate text".
    private const int Dims = 8;
    private static readonly float[] EmailVector = OneHot(0);
    private static readonly float[] ParisVector = OneHot(1);
    private const string EmailFact = "I prefer email over phone calls";
    private const string ParisFact = "The capital of France is Paris";
    private const string EmailQuery = "I prefer email over phone";

    private static FakeEmbedder CreateEmbedder() => new(new Dictionary<string, float[]>
    {
        [EmailFact] = EmailVector,
        ["I prefer email over phone calls."] = EmailVector, // near-duplicate rewrite
        [ParisFact] = ParisVector,
        [EmailQuery] = EmailVector,
    });

    [Fact]
    public async Task Recall_near_duplicate_query_returns_matching_fact_first()
    {
        var store = new FactMemoryStore(CreateEmbedder());
        var first = await store.AddAsync("u1", EmailFact);
        await store.AddAsync("u1", ParisFact);

        var results = await store.RecallAsync("u1", EmailQuery, topK: 3);

        Assert.Equal(2, results.Count);
        Assert.Equal(first.Id, results[0].Id);
        Assert.Contains("email", results[0].Text);
    }

    [Fact]
    public async Task AddAsync_duplicate_text_upserts_instead_of_adding_a_second_fact()
    {
        var store = new FactMemoryStore(CreateEmbedder());
        var original = await store.AddAsync("u1", EmailFact);
        var again = await store.AddAsync("u1", "I prefer email over phone calls.");

        Assert.Equal(EmailVector, again.Vector);
        Assert.Equal(original.Id, again.Id); // updated in place, not a new record

        var recalled = await store.RecallAsync("u1", EmailQuery, topK: 10);
        var fact = Assert.Single(recalled);
        Assert.Equal("I prefer email over phone calls.", fact.Text); // text was updated
    }

    [Fact]
    public async Task Save_then_Load_recalls_without_re_adding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FactMemoryStore(CreateEmbedder());
        var a = await store.AddAsync("u1", EmailFact);
        var b = await store.AddAsync("u1", ParisFact);
        await store.SaveAsync(path);

        var reloaded = new FactMemoryStore(CreateEmbedder()); // fresh instance, same file
        await reloaded.LoadAsync(path);

        var recalled = await reloaded.RecallAsync("u1", EmailQuery, topK: 3);
        Assert.Equal(2, recalled.Count);
        Assert.Equal(a.Id, recalled[0].Id);
        Assert.Contains("email", recalled[0].Text);
        Assert.Equal(b.Id, recalled[1].Id);
        File.Delete(path);
    }

    [Fact]
    public async Task LoadAsync_missing_file_starts_empty()
    {
        var store = new FactMemoryStore(CreateEmbedder());
        await store.LoadAsync(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json")); // must not throw
        Assert.Empty(await store.RecallAsync("u1", EmailQuery));
    }

    [Fact]
    public async Task AddAsync_unknown_text_never_collides_with_table_vectors()
    {
        var store = new FactMemoryStore(CreateEmbedder());
        await store.AddAsync("u1", EmailFact);
        await store.AddAsync("u1", "zzz qqq xxy"); // not in the table; must not dedupe-upsert EmailFact

        var recalled = await store.RecallAsync("u1", EmailQuery, topK: 10);
        Assert.Equal(2, recalled.Count);
    }

    [Fact]
    public async Task Recall_on_empty_store_returns_empty_list()
    {
        var store = new FactMemoryStore(CreateEmbedder());
        Assert.Empty(await store.RecallAsync("u1", EmailQuery));
    }

    [Fact]
    public async Task Recall_only_returns_the_given_users_facts()
    {
        var store = new FactMemoryStore(CreateEmbedder());
        await store.AddAsync("u1", EmailFact);
        await store.AddAsync("u2", ParisFact);

        var u1 = await store.RecallAsync("u1", EmailQuery, topK: 5);
        var u2 = await store.RecallAsync("u2", EmailQuery, topK: 5);

        _ = u2; // u2's recall uses an orthogonal query vector, so ordering is not asserted
        Assert.Equal(EmailFact, u1.Single().Text);
    }

    private static float[] OneHot(int index)
    {
        var v = new float[Dims];
        v[index] = 1f;
        return v;
    }

    /// <summary>
    /// Offline fake: same string always yields the same vector (table lookup).
    /// Unknown strings fall back to a one-hot drawn only from indices no table
    /// vector occupies, so an unknown input can never score cosine 1.0 against
    /// a known one and accidentally trigger dedupe-upsert. No model, no network.
    /// </summary>
    private sealed class FakeEmbedder(IReadOnlyDictionary<string, float[]> table) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly int[] _freeIndices = Enumerable.Range(0, Dims)
            .Except(table.Values.Select(v => Array.FindIndex(v, static x => x != 0)).Where(i => i >= 0))
            .ToArray();

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var embeddings = values.Select(text =>
            {
                if (table.TryGetValue(text, out var known))
                {
                    return new Embedding<float>(known);
                }

                // Deterministic within the process; never collides with a
                // table vector (falls back to the zero vector, which scores
                // cosine 0 against everything, if the table claimed all dims).
                if (_freeIndices.Length == 0)
                {
                    return new Embedding<float>(new float[Dims]);
                }

                var index = _freeIndices[(uint)text.GetHashCode() % _freeIndices.Length];
                return new Embedding<float>(OneHot(index));
            });
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
