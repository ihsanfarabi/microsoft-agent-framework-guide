// tests/P06.TriageComposition.Tests/SpecialistToolsTests.cs
using MafDemo.Core.Domain;
using MafDemo.Core.Handbook;
using MafDemo.Core.Stores;
using P06.TriageComposition;

// Tests never call live Ollama (ledger ruling): the retriever is built exactly
// the way tests/MafDemo.Core.Tests/HandbookRetrieverTests.cs does — a
// deterministic keyword embedder over inline handbook chunks — and injected
// into SpecialistTools alongside an InMemoryTicketStore.
public class KeywordEmbedder : IEmbedder   // deterministic: character count vector
{
    public Task<float[]> EmbedAsync(string text)
    {
        var v = new float[64];
        foreach (var word in text.ToLower().Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ch in word) v[ch % 64] += 1f;
        return Task.FromResult(v);
    }
}

public class SpecialistToolsTests
{
    private static readonly HandbookChunk[] Chunks =
    [
        new("onboarding.md", 0, "Employees get 25 vacation days per year."),
        new("vpn-policy.md", 0, "VPN reconnects must use MFA every 8 hours."),
        new("backup-policy.md", 0, "Backups run nightly at 2am to the Franklin region."),
    ];

    private static async Task<SpecialistTools> CreateToolsAsync(ITicketStore? store = null)
    {
        var retriever = new HandbookRetriever(new KeywordEmbedder());
        await retriever.BuildAsync(Chunks);
        return new SpecialistTools(store ?? new InMemoryTicketStore(), retriever);
    }

    [Fact]
    public async Task SearchHandbook_returns_matching_chunk_text()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.SearchHandbookAsync("vpn");

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("vpn-policy.md", result);
        Assert.Contains("MFA", result);
    }

    [Fact]
    public async Task SearchHandbook_never_returns_empty_string()
    {
        var tools = await CreateToolsAsync();

        // Even a nonsense query must come back as text the model can read,
        // never an empty string it could interpret as "nothing to do".
        var result = await tools.SearchHandbookAsync("quantum toaster warranty");

        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task GetTicket_round_trips_seeded_ticket_details()
    {
        var store = new InMemoryTicketStore();
        var seeded = await store.CreateAsync("Email stuck on outbox", "Exchange auth failure", TicketPriority.High);
        var tools = await CreateToolsAsync(store);

        var result = await tools.GetTicketAsync(seeded.Id.ToString());

        Assert.Contains(seeded.Id.ToString(), result);
        Assert.Contains("Email stuck on outbox", result);
        Assert.Contains("High", result);
    }

    [Fact]
    public async Task GetTicket_returns_not_found_for_unknown_id()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetTicketAsync(Guid.NewGuid().ToString());

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task GetTicket_rejects_malformed_id()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetTicketAsync("not-a-guid");

        Assert.Contains("Invalid ticket id", result);
    }

    [Fact]
    public async Task GetTicket_lists_notes()
    {
        var store = new InMemoryTicketStore();
        var seeded = await store.CreateAsync("Laptop dead", "no power", TicketPriority.Normal);
        await store.AddNoteAsync(seeded.Id, "Tried dock, same symptom");
        var tools = await CreateToolsAsync(store);

        var result = await tools.GetTicketAsync(seeded.Id.ToString());

        Assert.Contains("Tried dock, same symptom", result);
    }
}
