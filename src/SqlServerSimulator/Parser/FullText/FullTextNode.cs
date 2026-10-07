namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// One node of a parsed full-text search condition. Every node answers the
/// row-level question (<see cref="Matches"/>) and contributes its leaf terms to
/// the modeled <c>RANK</c> (<see cref="CollectLeaves"/>).
/// </summary>
internal abstract class FullTextNode
{
    public abstract bool Matches(FullTextDocument document);

    /// <summary>
    /// Adds this subtree's leaf terms to <paramref name="into"/>, multiplying
    /// through whatever <c>ISABOUT</c> weight encloses them. The rank model
    /// reads nothing else from the tree.
    /// </summary>
    public abstract void CollectLeaves(List<(FullTextTermNode Leaf, double Weight)> into, double weight);

    /// <summary>
    /// True when the engine ignored this node entirely — every term in it was a
    /// system stopword. Real doesn't merely fail to match such a node, it
    /// collapses the clause holding it: <c>quick AND NOT the</c> returns no rows
    /// even though <c>quick</c> matches and <c>the</c> excludes nothing.
    /// </summary>
    public virtual bool IsIgnored => false;

    /// <summary>
    /// A condition that reduces to nothing — every term was a stopword, or a
    /// term word-broke to no term at all. Real answers such a query with no
    /// rows.
    /// </summary>
    public static readonly FullTextNeverMatchNode NeverMatches = new();
}

/// <inheritdoc cref="FullTextNode.NeverMatches"/>
internal sealed class FullTextNeverMatchNode : FullTextNode
{
    public override bool Matches(FullTextDocument document) => false;

    public override bool IsIgnored => true;

    public override void CollectLeaves(List<(FullTextTermNode Leaf, double Weight)> into, double weight)
    {
    }
}

/// <summary>
/// One position of a leaf: the terms the word breaker put there — a word, or a
/// word with its companions (<c>42</c> and <c>nn42</c>, a compound's composite
/// and its first part) — any one of which matches, at its offset from the
/// leaf's first position.
/// </summary>
internal readonly struct FullTextElement(int offset, string[] alternatives, bool prefix, bool wildcard)
{
    public readonly int Offset = offset;

    /// <summary>
    /// The terms that match at this position: noise words left out unless the
    /// position is starred, and a single-letter noise word replaced by its
    /// forms in an inflectional leaf.
    /// </summary>
    public readonly string[] Alternatives = alternatives;

    /// <summary>A <c>"word*"</c> element, matching any term the alternative starts.</summary>
    public readonly bool Prefix = prefix;

    /// <summary>
    /// Every term here was a noise word, which constrains nothing but the
    /// distance between the terms around it: <c>"jumps over lazy"</c> matches
    /// nothing in text reading <c>jumps over the lazy</c>, while a noise word
    /// leading or trailing a phrase drops out — <c>"with ships"</c> finds
    /// <c>ships</c> as a document's first word (probed 2026-09-29 against SQL
    /// Server 2025).
    /// </summary>
    public readonly bool Wildcard = wildcard;
}

/// <summary>One place a leaf matched: its first and last position.</summary>
internal readonly struct FullTextOccurrence(int start, int end)
{
    public readonly int Start = start;
    public readonly int End = end;
}

/// <summary>
/// A leaf: one word, one prefix, or one phrase, as the word breaker read it —
/// a run of positions, each with the terms it may hold.
/// </summary>
internal sealed class FullTextTermNode : FullTextNode
{
    private readonly FullTextElement[] elements;
    private readonly bool inflectional;

    private FullTextTermNode(FullTextElement[] elements, bool inflectional)
    {
        this.elements = elements;
        this.inflectional = inflectional;
    }

    public static FullTextTermNode Create(FullTextElement[] elements, bool inflectional) => new(elements, inflectional);

    /// <summary>
    /// Builds a single-word leaf that already carries its inflectional flag —
    /// the shape <c>FREETEXT</c> produces for each surviving word.
    /// </summary>
    public static FullTextTermNode Word(string term, bool inflectional) =>
        new([new FullTextElement(0, [term], prefix: false, wildcard: false)], inflectional);

    public override bool Matches(FullTextDocument document) => Occurrences(document).Count > 0;

    /// <summary>True when every position was noise, which the engine drops.</summary>
    public override bool IsIgnored
    {
        get
        {
            foreach (var element in this.elements)
            {
                if (!element.Wildcard)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Every place this leaf matches, by first position ascending, each
    /// spanning the whole phrase — what <c>NEAR</c> measures between.
    /// </summary>
    public List<FullTextOccurrence> Occurrences(FullTextDocument document)
    {
        List<FullTextOccurrence> occurrences = [];
        var anchor = 0;
        while (anchor < this.elements.Length && this.elements[anchor].Wildcard)
            anchor++;
        if (anchor == this.elements.Length)
            return occurrences;
        var previous = int.MinValue;
        foreach (var start in PositionsOf(document, this.elements[anchor]))
        {
            if (start == previous)
                continue;
            previous = start;
            // An occurrence spans the whole phrase, noise positions at its
            // ends included: `UTF-8` covers `utf` and `8`, so two terms lie
            // between it and `account` in `UTF-8 bike customer account`.
            if (MatchFrom(document, anchor, start))
            {
                var first = start - this.elements[anchor].Offset;
                occurrences.Add(new FullTextOccurrence(first, first + this.elements[^1].Offset));
            }
        }
        return occurrences;
    }

    /// <summary>
    /// True when the elements from <paramref name="index"/> on match, the
    /// first at <paramref name="position"/>. Each position is matched by one
    /// of its own terms: a compound's composite stands for its first position
    /// only, so a phrase still needs the parts after it — real finds no
    /// <c>August 2, 2026</c> for <c>"2026-08-02"</c> although both carry
    /// <c>dd20260802</c>.
    /// </summary>
    private bool MatchFrom(FullTextDocument document, int index, int position)
    {
        for (; index < this.elements.Length; index++)
        {
            var element = this.elements[index];
            if (!element.Wildcard && !HoldsAny(document, element, position))
                return false;
            if (index + 1 < this.elements.Length)
                position += this.elements[index + 1].Offset - element.Offset;
        }
        return true;
    }

    private bool HoldsAny(FullTextDocument document, FullTextElement element, int position)
    {
        foreach (var alternative in element.Alternatives)
        {
            if (Holds(document, alternative, element.Prefix, position))
                return true;
        }
        return false;
    }

    private bool Holds(FullTextDocument document, string term, bool prefix, int position) =>
        prefix ? document.HoldsPrefix(term, position)
        : this.inflectional ? HoldsStems(document, FullTextLexicon.Stems(term), position)
        : document.HoldsExact(term, position);

    /// <summary>Every position holding one of the element's terms, ascending.</summary>
    private List<int> PositionsOf(FullTextDocument document, FullTextElement element)
    {
        if (element.Alternatives.Length == 1)
            return PositionsOf(document, element.Alternatives[0], element.Prefix);
        var merged = new SortedSet<int>();
        foreach (var alternative in element.Alternatives)
            merged.UnionWith(PositionsOf(document, alternative, element.Prefix));
        return [.. merged];
    }

    private List<int> PositionsOf(FullTextDocument document, string term, bool prefix) =>
        prefix ? document.Prefixed(term)
        : this.inflectional ? StemmedPositions(document, FullTextLexicon.Stems(term))
        : document.Exact(term);

    private static bool HoldsStems(FullTextDocument document, (string Primary, string? Secondary) stems, int position) =>
        document.HoldsStem(stems.Primary, position) || (stems.Secondary is { } secondary && document.HoldsStem(secondary, position));

    /// <summary>The positions holding either of a term's stems, ascending.</summary>
    private static List<int> StemmedPositions(FullTextDocument document, (string Primary, string? Secondary) stems)
    {
        var primary = document.Stemmed(stems.Primary);
        if (stems.Secondary is not { } secondary || document.Stemmed(secondary) is not { Count: > 0 } other)
            return primary;
        var merged = new SortedSet<int>(primary);
        merged.UnionWith(other);
        return [.. merged];
    }

    /// <summary>
    /// How many times this leaf occurs — the <c>tf</c> the rank model reads.
    /// </summary>
    public int TermFrequency(FullTextDocument document) => Occurrences(document).Count;

    public override void CollectLeaves(List<(FullTextTermNode Leaf, double Weight)> into, double weight) =>
        into.Add((this, weight));
}

/// <summary>
/// <c>AND</c> / <c>OR</c> / <c>AND NOT</c>, and the <c>ISABOUT</c> list, which
/// is an OR carrying a per-branch rank weight.
/// </summary>
internal sealed class FullTextBooleanNode : FullTextNode
{
    private readonly FullTextNode[] operands;
    private readonly double[]? operandWeights;
    private readonly BooleanKind kind;

    private FullTextBooleanNode(FullTextNode[] operands, double[]? operandWeights, BooleanKind kind)
    {
        this.operands = operands;
        this.operandWeights = operandWeights;
        this.kind = kind;
    }

    public static FullTextNode And(List<FullTextNode> operands) =>
        new FullTextBooleanNode([.. operands], null, BooleanKind.And);

    public static FullTextNode Or(List<FullTextNode> operands) =>
        new FullTextBooleanNode([.. operands], null, BooleanKind.Or);

    /// <summary>
    /// <c>AND NOT</c>, except that an ignored excluded operand collapses the
    /// whole clause — real's behavior, probe-confirmed with
    /// <c>'quick AND NOT the'</c>, which returns nothing.
    /// </summary>
    public static FullTextNode AndNot(FullTextNode left, FullTextNode right) =>
        right.IsIgnored ? NeverMatches : new FullTextBooleanNode([left, right], null, BooleanKind.AndNot);

    public static FullTextNode Weighted(List<FullTextNode> operands, double[] weights) =>
        new FullTextBooleanNode([.. operands], weights, BooleanKind.Or);

    public override bool Matches(FullTextDocument document)
    {
        switch (this.kind)
        {
            case BooleanKind.And:
                foreach (var operand in this.operands)
                {
                    if (!operand.Matches(document))
                        return false;
                }
                return true;

            case BooleanKind.AndNot:
                return this.operands[0].Matches(document) && !this.operands[1].Matches(document);

            default:
                foreach (var operand in this.operands)
                {
                    if (operand.Matches(document))
                        return true;
                }
                return false;
        }
    }

    public override void CollectLeaves(List<(FullTextTermNode Leaf, double Weight)> into, double weight)
    {
        // The excluded side of AND NOT contributes nothing to rank — it
        // narrows the row set rather than describing what was found.
        var contributing = this.kind == BooleanKind.AndNot ? 1 : this.operands.Length;
        for (var i = 0; i < contributing; i++)
            this.operands[i].CollectLeaves(into, weight * (this.operandWeights is null ? 1.0 : this.operandWeights[i]));
    }

    private enum BooleanKind
    {
        And,
        AndNot,
        Or,
    }
}

/// <summary>
/// <c>NEAR</c> in both spellings. All operands must occur, and — when a
/// distance is given — some arrangement of one occurrence each must fit inside
/// it, counting the terms lying between neighbouring operands. With
/// <c>ordered</c> the arrangement must also run left to right in the written
/// order.
/// </summary>
internal sealed class FullTextProximityNode : FullTextNode
{
    private readonly FullTextTermNode[] terms;
    private readonly int? maximumDistance;
    private readonly bool ordered;

    private FullTextProximityNode(FullTextTermNode[] terms, int? maximumDistance, bool ordered)
    {
        this.terms = terms;
        this.maximumDistance = maximumDistance;
        this.ordered = ordered;
    }

    /// <summary>
    /// Builds the node, or falls back to a plain AND when an operand isn't a
    /// simple term (a nested boolean has no single position to measure from).
    /// </summary>
    public static FullTextNode Create(List<FullTextNode> operands, int? maximumDistance, bool ordered)
    {
        var terms = new FullTextTermNode[operands.Count];
        for (var i = 0; i < operands.Count; i++)
        {
            if (operands[i] is not FullTextTermNode term)
                return FullTextBooleanNode.And(operands);
            terms[i] = term;
        }
        return new FullTextProximityNode(terms, maximumDistance, ordered);
    }

    public override bool Matches(FullTextDocument document)
    {
        var occurrences = new List<FullTextOccurrence>[this.terms.Length];
        for (var i = 0; i < this.terms.Length; i++)
        {
            occurrences[i] = this.terms[i].Occurrences(document);
            if (occurrences[i].Count == 0)
                return false;
        }
        // The ordered form still constrains the sequence when the distance is
        // MAX or absent (probe: `NEAR((bbb, aaa), MAX, TRUE)` matched only the
        // row spelling them that way round). Either form needs one
        // occurrence per operand, no two overlapping, so the same term named
        // twice needs two occurrences.
        return this.ordered
            ? SearchOrdered(occurrences, this.maximumDistance, 0, previousEnd: int.MinValue)
            : SearchUnordered(occurrences, this.maximumDistance ?? int.MaxValue);
    }

    /// <summary>
    /// Walks one occurrence per operand in the written order, each starting
    /// after the previous ends and — when bounded — within the distance of it.
    /// </summary>
    private static bool SearchOrdered(List<FullTextOccurrence>[] occurrences, int? limit, int index, int previousEnd)
    {
        if (index == occurrences.Length)
            return true;
        foreach (var candidate in occurrences[index])
        {
            if (previousEnd != int.MinValue)
            {
                if (candidate.Start <= previousEnd)
                    continue;
                if (limit is { } bound && candidate.Start - previousEnd - 1 > bound)
                    continue;
            }
            if (SearchOrdered(occurrences, limit, index + 1, candidate.End))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Any permutation counts: the terms lying between the operands — the
    /// span from the first start to the last end, less the operands' own
    /// lengths — must number no more than <paramref name="bound"/>.
    /// </summary>
    private static bool SearchUnordered(List<FullTextOccurrence>[] occurrences, int bound)
    {
        var chosen = new FullTextOccurrence[occurrences.Length];
        return Choose(occurrences, bound, 0, chosen);

        static bool Choose(List<FullTextOccurrence>[] occurrences, int bound, int index, FullTextOccurrence[] chosen)
        {
            if (index == occurrences.Length)
            {
                var lowest = int.MaxValue;
                var highest = int.MinValue;
                var covered = 0L;
                foreach (var occurrence in chosen)
                {
                    lowest = Math.Min(lowest, occurrence.Start);
                    highest = Math.Max(highest, occurrence.End);
                    covered += occurrence.End - occurrence.Start + 1;
                }
                return (long)highest - lowest + 1 - covered <= bound;
            }
            foreach (var candidate in occurrences[index])
            {
                var overlaps = false;
                for (var i = 0; i < index; i++)
                {
                    if (candidate.Start <= chosen[i].End && chosen[i].Start <= candidate.End)
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (overlaps)
                    continue;
                chosen[index] = candidate;
                if (Choose(occurrences, bound, index + 1, chosen))
                    return true;
            }
            return false;
        }
    }

    public override void CollectLeaves(List<(FullTextTermNode Leaf, double Weight)> into, double weight)
    {
        foreach (var term in this.terms)
            term.CollectLeaves(into, weight);
    }
}
