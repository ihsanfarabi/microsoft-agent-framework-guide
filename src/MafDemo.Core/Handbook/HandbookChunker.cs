namespace MafDemo.Core.Handbook;

public static class HandbookChunker
{
    public static IReadOnlyList<HandbookChunk> Chunk(string doc, string text, int maxChars = 500)
    {
        var chunks = new List<HandbookChunk>();
        var current = "";
        int index = 0;
        void Flush() { if (current.Trim().Length > 0) chunks.Add(new(doc, index++, current.Trim())); current = ""; }

        foreach (var para in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var p = para.Trim();
            if (p.Length > maxChars)
            {
                Flush();
                for (int i = 0; i < p.Length; i += maxChars)
                    chunks.Add(new(doc, index++, p.Substring(i, Math.Min(maxChars, p.Length - i))));
                continue;
            }
            if (current.Length + p.Length + 2 > maxChars) Flush();
            current = current.Length == 0 ? p : current + "\n\n" + p;
        }
        Flush();
        return chunks;
    }
}
