namespace P14.SemanticMemory.Memory;

/// <summary>
/// Startup helper for loading a persisted <see cref="MafDemo.Core.Memory.FactMemoryStore"/>
/// from its JSON file. Repo convention (established in P08): startup must survive
/// corrupt persisted state. <see cref="MafDemo.Core.Memory.FactMemoryStore.LoadAsync"/>
/// itself quarantines a corrupt file and starts empty; this helper is the outer
/// guard for anything else that can go wrong reading the file (permissions, IO
/// errors), degrading to an empty store instead of crashing the host.
/// </summary>
public static class FactStoreStartup
{
    /// <summary>
    /// Loads <paramref name="path"/> into <paramref name="store"/>. A missing
    /// file is a normal empty store (true). A corrupt or unreadable file is
    /// reported through <paramref name="warn"/> (defaults to
    /// <see cref="Console.Error"/>) and the store starts empty — never throws.
    /// </summary>
    public static async Task<bool> TryLoadAsync(
        MafDemo.Core.Memory.FactMemoryStore store, string path, Action<string>? warn = null)
    {
        try
        {
            await store.LoadAsync(path);
            return true;
        }
        catch (Exception ex)
        {
            (warn ?? Console.Error.WriteLine)(
                $"Ignoring unreadable facts file '{path}': {ex.Message}. Starting with an empty store.");
            return false;
        }
    }
}
