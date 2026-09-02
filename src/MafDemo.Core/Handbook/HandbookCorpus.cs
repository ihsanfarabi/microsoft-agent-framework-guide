namespace MafDemo.Core.Handbook;

public static class HandbookCorpus
{
    /// <summary>Locates the shared handbook corpus (docs/corpus) by walking up
    /// from the binary location until the directory appears — dotnet run
    /// executes from bin/&lt;cfg&gt;/net10.0, so the repo root is several levels
    /// up. Robust regardless of Debug/Release or publish layout.</summary>
    public static DirectoryInfo Locate(string? overridePath = null)
    {
        if (overridePath is not null && Directory.Exists(overridePath))
            return new DirectoryInfo(overridePath);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "docs", "corpus");
            if (Directory.Exists(probe))
                return new DirectoryInfo(probe);
        }

        throw new DirectoryNotFoundException(
            $"could not find docs/corpus in any parent of {AppContext.BaseDirectory}");
    }
}
