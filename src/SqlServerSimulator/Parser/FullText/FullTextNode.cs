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
/// A leaf: one word, one prefix, or one phrase. A phrase element that is a
/// system stopword matches whatever term occupies that position — real's
/// behavior, which is why <c>"over the lazy dog"</c> matches text reading
/// exactly that and <c>"jumps over lazy"</c> matches nothing in text reading
/// <c>jumps over the lazy</c>.
/// </summary>
internal sealed class FullTextTermNode : FullTextNode
{
    private readonly string[] elements;

    /// <summary>
    /// Per-element prefix flags. A quoted term carries one for each of its
    /// whitespace-separated words that was written with a trailing star, so
    /// <c>"al* be*"</c> asks for two prefixes rather than one.
    /// </summary>
    private readonly bool[] prefixes;
    private readonly bool inflectional;

    private FullTextTermNode(string[] elements, bool[] prefixes, bool inflectional)
    {
        this.elements = elements;
        this.prefixes = prefixes;
        this.inflectional = inflectional;
    }

    public static FullTextTermNode Create(string[] elements, bool[] prefixes, bool inflectional) =>
        new(elements, prefixes, inflectional);

    /// <summary>
    /// Builds a single-word leaf that already carries its inflectional flag —
    /// the shape <c>FREETEXT</c> produces for each surviving word.
    /// </summary>
    public static FullTextTermNode Word(string term, bool inflectional) =>
        new([term], [false], inflectional);

    public override bool Matches(FullTextDocument document) => StartPositions(document).Count > 0;

    /// <summary>
    /// True when every element was a stopword the engine dropped. A phrase
    /// holding at least one real term keeps its stopword elements as position
    /// wildcards instead.
    /// </summary>
    public override bool IsIgnored
    {
        get
        {
            for (var i = 0; i < this.elements.Length; i++)
            {
                if (this.prefixes[i] || !FullTextLexicon.IsStopword(this.elements[i]))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Positions where this leaf begins. A single word reports every position
    /// it occupies; a phrase reports the position of each occurrence's first
    /// element, which is what <c>NEAR</c> measures from.
    /// </summary>
    public List<int> StartPositions(FullTextDocument document)
    {
        List<int> starts = [];
        if (this.IsIgnored)
            return starts;

        var onlyElement = this.elements.Length == 1;
        if (IsWildcardElement(0))
        {
            // A leading stopword is a wildcard, so every occupied position is a
            // candidate start.
            for (var position = 1; position <= document.MaxPosition; position++)
            {
                if (document.IsOccupied(position) && MatchesFrom(document, position))
                    starts.Add(position);
            }
            return starts;
        }

        foreach (var position in PositionsOfElement(document, 0))
        {
            if (onlyElement || MatchesFrom(document, position))
                starts.Add(position);
        }
        return starts;
    }

    /// <summary>
    /// True when the element matches whatever term occupies its position — a
    /// stopword the engine dropped, unless the writer asked for a prefix there.
    /// </summary>
    private bool IsWildcardElement(int elementIndex) =>
        !this.prefixes[elementIndex] && FullTextLexicon.IsStopword(this.elements[elementIndex]);

    private bool MatchesFrom(FullTextDocument document, int start)
    {
        for (var i = 1; i < this.elements.Length; i++)
        {
            if (!ElementMatchesAt(document, i, start + i))
                return false;
        }
        return true;
    }

    private List<int> PositionsOfElement(FullTextDocument document, int elementIndex)
    {
        var element = this.elements[elementIndex];
        return this.prefixes[elementIndex] ? document.Prefixed(element)
            : this.inflectional ? document.Stemmed(FullTextLexicon.Stem(element))
            : document.Exact(element);
    }

    private bool ElementMatchesAt(FullTextDocument document, int elementIndex, int position)
    {
        if (IsWildcardElement(elementIndex))
            return document.IsOccupied(position);
        foreach (var candidate in PositionsOfElement(document, elementIndex))
        {
            if (candidate == position)
                return true;
        }
        return false;
    }

    /// <summary>
    /// How many times this leaf occurs — the <c>tf</c> the rank model reads.
    /// </summary>
    public int TermFrequency(FullTextDocument document) => StartPositions(document).Count;

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
        var positions = new List<int>[this.terms.Length];
        for (var i = 0; i < this.terms.Length; i++)
        {
            positions[i] = this.terms[i].StartPositions(document);
            if (positions[i].Count == 0)
                return false;
        }
        // The ordered form still constrains the sequence when the distance is
        // MAX or absent (probe: `NEAR((bbb, aaa), MAX, TRUE)` matched only the
        // row spelling them that way round); the unordered form with no bound
        // asks nothing beyond "both occur in this row".
        return this.ordered
            ? SearchOrdered(positions, this.maximumDistance, 0, previous: int.MinValue)
            : this.maximumDistance is not { } bound || SearchUnordered(positions, bound);
    }

    /// <summary>
    /// Walks one occurrence per operand in the written order, each sitting
    /// after the previous and — when bounded — within the distance of it.
    /// </summary>
    private static bool SearchOrdered(List<int>[] positions, int? limit, int index, int previous)
    {
        if (index == positions.Length)
            return true;
        foreach (var candidate in positions[index])
        {
            if (previous != int.MinValue)
            {
                if (candidate <= previous)
                    continue;
                if (limit is { } bound && candidate - previous - 1 > bound)
                    continue;
            }
            if (SearchOrdered(positions, limit, index + 1, candidate))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Any permutation counts: the span between the smallest and largest chosen
    /// positions must leave no more than <paramref name="bound"/> terms between
    /// the operands.
    /// </summary>
    private static bool SearchUnordered(List<int>[] positions, int bound)
    {
        var chosen = new int[positions.Length];
        return Choose(positions, bound, 0, chosen);

        static bool Choose(List<int>[] positions, int bound, int index, int[] chosen)
        {
            if (index == positions.Length)
            {
                var lowest = chosen[0];
                var highest = chosen[0];
                foreach (var value in chosen)
                {
                    if (value < lowest)
                        lowest = value;
                    if (value > highest)
                        highest = value;
                }
                // The bound counts the terms lying between the two ends, so an
                // adjacent pair spans one position and leaves zero between.
                return highest - lowest - (positions.Length - 1) <= bound;
            }
            foreach (var candidate in positions[index])
            {
                chosen[index] = candidate;
                if (Choose(positions, bound, index + 1, chosen))
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
