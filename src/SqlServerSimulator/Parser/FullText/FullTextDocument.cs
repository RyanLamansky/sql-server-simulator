namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// The positional term index of one row's full-text-indexed content, built by
/// running <see cref="FullTextWordBreaker"/> over each indexed column and
/// concatenating the results. A search condition tests against this.
/// </summary>
/// <remarks>
/// <para>
/// The simulator builds this <b>while reading</b> rather than maintaining a
/// persisted inverted index across DML. A row's terms therefore always reflect
/// the row as the reading transaction sees it — rollback, MVCC snapshots,
/// triggers and cross-database writes all need no separate bookkeeping, and a
/// row is searchable the instant it is written. Real crawls asynchronously
/// under <c>CHANGE_TRACKING AUTO</c>, so it reaches the same answer a few
/// seconds later; <c>docs/claude/full-text.md</c> records the lag as the
/// divergence.
/// </para>
/// <para>
/// Multiple columns share one position sequence, laid end to end in the
/// index's column order. A phrase or <c>NEAR</c> therefore cannot span two
/// columns in the simulator either, because each column's run starts a gap
/// wide enough that no adjacency crosses it.
/// </para>
/// </remarks>
internal sealed class FullTextDocument
{
    private readonly Dictionary<string, List<int>> exactPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> stemPositions = new(StringComparer.Ordinal);
    private readonly HashSet<int> occupiedPositions = [];

    /// <summary>Highest position any term occupies; 0 for an empty document.</summary>
    public int MaxPosition;

    /// <summary>
    /// Adds one column's content. <paramref name="gap"/> positions are left
    /// between columns so no phrase or proximity match straddles a column
    /// boundary.
    /// </summary>
    public void AddColumn(string? text, bool accentSensitive, int gap = 1000)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var offset = this.MaxPosition == 0 ? 0 : this.MaxPosition + gap;
        foreach (var term in FullTextWordBreaker.Break(text, accentSensitive))
        {
            var position = offset + term.Position;
            Append(this.exactPositions, term.Text, position);
            Append(this.stemPositions, FullTextLexicon.Stem(term.Text), position);
            _ = this.occupiedPositions.Add(position);
            if (position > this.MaxPosition)
                this.MaxPosition = position;
        }
    }

    private static void Append(Dictionary<string, List<int>> index, string key, int position)
    {
        if (!index.TryGetValue(key, out var positions))
        {
            positions = [];
            index[key] = positions;
        }
        // The composite of a compound shares its first part's position, and a
        // repeated word lands at ascending positions, so a duplicate entry is
        // only possible when the same key arrives twice at one position.
        if (positions.Count == 0 || positions[^1] != position)
            positions.Add(position);
    }

    /// <summary>Positions of an exact term, or an empty list.</summary>
    public IReadOnlyList<int> Exact(string term) =>
        this.exactPositions.TryGetValue(term, out var positions) ? positions : [];

    /// <summary>Positions whose term stems to <paramref name="stem"/>.</summary>
    public IReadOnlyList<int> Stemmed(string stem) =>
        this.stemPositions.TryGetValue(stem, out var positions) ? positions : [];

    /// <summary>
    /// Positions of every term starting with <paramref name="prefix"/>, in
    /// ascending order — the <c>"term*"</c> form's reach.
    /// </summary>
    public List<int> Prefixed(string prefix)
    {
        List<int> matches = [];
        foreach (var (term, positions) in this.exactPositions)
        {
            if (term.StartsWith(prefix, StringComparison.Ordinal))
                matches.AddRange(positions);
        }
        matches.Sort();
        return matches;
    }

    /// <summary>True when any term occupies <paramref name="position"/>.</summary>
    public bool IsOccupied(int position) => this.occupiedPositions.Contains(position);

    /// <summary>
    /// How many terms the document holds, counting a compound's composite and
    /// its parts once each. Feeds the modeled <c>RANK</c>'s length
    /// normalization.
    /// </summary>
    public int Length => this.occupiedPositions.Count;
}
