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
}
