namespace MafDemo.Core.Handbook;

/// <summary>
/// Token-overlap relevance scorer for the IT handbook corpus (P12).
/// No embeddings — P04 owns the vector path. Scoring is TF-free "BM25-lite":
/// distinct query tokens present in the text, divided by sqrt(|query tokens|).
/// </summary>
public static class KnowledgeScorer
{
    /// <summary>
    /// Scores <paramref name="text"/> against <paramref name="query"/>.
    /// Returns overlap of distinct query tokens with text tokens divided by
    /// sqrt(number of distinct query tokens); 0 for an empty/whitespace/
    /// punctuation-only query or when nothing matches. Never throws.
    /// </summary>
    public static double Score(string query, string text)
    {
        var queryTokens = DistinctTokens(query);
        if (queryTokens.Count == 0) return 0;

        var textTokens = new HashSet<string>(Tokens(text));
        int overlap = 0;
        foreach (var t in queryTokens)
            if (textTokens.Contains(t)) overlap++;

        return overlap / Math.Sqrt(queryTokens.Count);
    }

    /// <summary>
    /// Scores every chunk, orders descending, and returns up to
    /// <paramref name="maxResults"/> lines of "doc | score | excerpt"
    /// (excerpt = first ~120 chars, whitespace-normalized).
    /// Ties on score are broken by raw query-token frequency in the text
    /// (denser matches first), then by input order, so ranking is deterministic.
    /// Returns "" (never throws) for an empty/whitespace/punctuation-only query,
    /// a non-positive <paramref name="maxResults"/>, or an empty chunk list;
    /// with a valid query and no matches it returns zero-score lines.
    /// </summary>
    public static string Search(string query, int maxResults, IReadOnlyList<(string Doc, string Text)> chunks)
    {
        if (maxResults <= 0 || chunks.Count == 0) return "";
        var queryTokens = DistinctTokens(query);
        if (queryTokens.Count == 0) return "";

        var ranked = new List<(int Index, string Doc, string Text, double Score, int Frequency)>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var (doc, text) = chunks[i];
            var textTokens = Tokens(text);
            var set = new HashSet<string>(textTokens);
            int overlap = queryTokens.Count(set.Contains);
            int frequency = queryTokens.Sum(t => textTokens.Count(t2 => t2 == t));
            ranked.Add((i, doc, text, overlap / Math.Sqrt(queryTokens.Count), frequency));
        }

        return string.Join("\n", ranked
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Frequency)
            .ThenBy(r => r.Index)
            .Take(maxResults)
            .Select(r => $"{r.Doc} | {r.Score:F2} | {Excerpt(r.Text)}"));
    }

    private static string Excerpt(string text)
    {
        var normalized = string.Join(' ', text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static List<string> DistinctTokens(string? text)
        => Tokens(text).Distinct().ToList();

    private static IEnumerable<string> Tokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
