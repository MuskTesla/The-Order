using System.Collections.Immutable;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Recognition;

public sealed partial class TitleIndex
{
    public async Task<ImmutableArray<IndexCandidate>> FindSimilarAsync(ImmutableArray<string> names, CancellationToken cancellation)
    {
        if (names.Length > 6 || names.Any(n => n.Length > 500)) throw new InvalidDataException("相近标题查询超过上限。");
        if (names.IsEmpty) return [];
        await FindAsync([], cancellation).ConfigureAwait(false);
        var snapshot = current!;
        return await Task.Run(() =>
        {
            var matches = new Dictionary<int, (string Observed, int Distance, int Length)>();
            foreach (var name in names.DistinctBy(TitleMatching.IndexKey, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                var key = TitleMatching.IndexKey(name);
                // Short identities have many unrelated neighbors; exact retrieval remains their path.
                if (key.Length is < 7 or > 160 || snapshot.Names.ContainsKey(key)) continue;
                var limit = Math.Clamp(key.Length / 16, 1, 3);
                foreach (var (candidate, rows) in snapshot.Names)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (Math.Abs(candidate.Length - key.Length) > limit) continue;
                    var distance = DistanceAtMost(key, candidate, limit);
                    if (distance > limit || 1d - (double)distance / Math.Max(candidate.Length, key.Length) < .85) continue;
                    var comparison = TitleMatching.Compare(name, snapshot.Rows[rows[0]].Names.First(n => TitleMatching.IndexKey(n) == candidate));
                    if (comparison.Conflict is not null || comparison.Version != "same" || !comparison.Distinctive) continue;
                    foreach (var row in rows)
                        if (!matches.TryGetValue(row, out var prior) || distance < prior.Distance)
                            matches[row] = (name, distance, Math.Max(key.Length, candidate.Length));
                }
            }
            cancellation.ThrowIfCancellationRequested();
            // Deterministic ordering allocates retrieval budget, never chooses an automatic identity.
            return matches.OrderBy(p => (double)p.Value.Distance / p.Value.Length).ThenBy(p => p.Key).Take(24)
                .Select(p => new IndexCandidate(p.Key, snapshot.Rows[p.Key], p.Value.Observed, false)).ToImmutableArray();
        }, cancellation).ConfigureAwait(false);
    }

    // Independently implemented bounded Levenshtein. Two stack buffers; no per-cell objects or persistent fuzzy cache.
    private static int DistanceAtMost(ReadOnlySpan<char> left, ReadOnlySpan<char> right, int limit)
    {
        while (!left.IsEmpty && !right.IsEmpty && left[0] == right[0]) { left = left[1..]; right = right[1..]; }
        while (!left.IsEmpty && !right.IsEmpty && left[^1] == right[^1]) { left = left[..^1]; right = right[..^1]; }
        if (left.IsEmpty) return right.Length;
        if (right.IsEmpty) return left.Length;
        if (Math.Abs(left.Length - right.Length) > limit) return limit + 1;
        Span<int> previous = stackalloc int[right.Length + 1];
        Span<int> currentRow = stackalloc int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = Math.Min(j, limit + 1);
        for (var i = 1; i <= left.Length; i++)
        {
            currentRow.Fill(limit + 1); currentRow[0] = i;
            var minimum = limit + 1;
            for (var j = Math.Max(1, i - limit); j <= Math.Min(right.Length, i + limit); j++)
            {
                currentRow[j] = Math.Min(previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1), Math.Min(previous[j] + 1, currentRow[j - 1] + 1));
                minimum = Math.Min(minimum, currentRow[j]);
            }
            if (minimum > limit) return limit + 1;
            var swap = previous; previous = currentRow; currentRow = swap;
        }
        return previous[right.Length];
    }
}
