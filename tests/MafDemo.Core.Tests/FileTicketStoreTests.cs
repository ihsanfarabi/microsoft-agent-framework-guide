using MafDemo.Core.Domain;
using MafDemo.Core.Stores;

public class FileTicketStoreTests
{
    [Fact]
    public async Task Create_persists_to_disk_and_roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileTicketStore(path);
        var created = await store.CreateAsync("VPN", "broken", TicketPriority.High);

        var reloaded = new FileTicketStore(path);      // fresh instance, same file
        var loaded = await reloaded.GetAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal("VPN", loaded!.Title);
        File.Delete(path);
    }

    [Fact]
    public async Task AddNote_survives_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileTicketStore(path);
        var t = await store.CreateAsync("t", "d", TicketPriority.Normal);
        await store.AddNoteAsync(t.Id, "restarted laptop");

        var reloaded = new FileTicketStore(path);
        var loaded = await reloaded.GetAsync(t.Id);
        Assert.Contains("restarted laptop", loaded!.Notes);
        File.Delete(path);
    }

    [Fact]
    public async Task Missing_file_starts_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileTicketStore(path);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task AddNote_unknown_id_returns_false()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        Assert.False(await new FileTicketStore(path).AddNoteAsync(Guid.NewGuid(), "x"));
    }

    [Fact]
    public async Task Concurrent_create_and_list_do_not_throw_or_lose_tickets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileTicketStore(path);

        // Two racing writers on one singleton store: both must succeed, and a
        // concurrent reader must never observe a torn Dictionary (which throws
        // or, under a shared tmp file, IOException from File.Move).
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => store.CreateAsync("t", "d", TicketPriority.Normal)));
        var created = await Task.WhenAll(tasks);
        var listed = await store.ListAsync();

        Assert.Equal(8, listed.Count);                        // nothing lost
        Assert.Equal(8, listed.Select(t => t.Id).Distinct().Count()); // no id collisions
        Assert.True(File.Exists(path));
        File.Delete(path);
    }

    [Fact]
    public async Task Duplicate_id_file_starts_empty_instead_of_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var id = Guid.NewGuid();
        var dup = $$"""
            [
              {"Id":"{{id}}","Title":"a","Description":"d","Priority":0,"Status":0,"Notes":[]},
              {"Id":"{{id}}","Title":"b","Description":"d","Priority":0,"Status":0,"Notes":[]}
            ]
            """;
        File.WriteAllText(path, dup);

        var store = new FileTicketStore(path);        // must not throw (ArgumentException today)
        Assert.Empty(await store.ListAsync());
        // The unusable file is preserved for inspection, same as the corrupt case.
        Assert.True(File.Exists(path + ".corrupt"));
        File.Delete(path + ".corrupt");
    }

    [Fact]
    public async Task Corrupt_file_starts_empty_instead_of_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{ not json");

        var store = new FileTicketStore(path);       // must not throw
        Assert.Empty(await store.ListAsync());
        // The corrupt data is preserved, not silently destroyed.
        Assert.True(File.Exists(path + ".corrupt"));
        File.Delete(path + ".corrupt");
    }
}
