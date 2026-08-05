namespace SqlServerSimulator.Parser;

/// <summary>
/// What <see cref="Expression.VisitColumnReferences(ColumnReferenceVisitor)"/>
/// carries down a tree: the action each column reference is handed to, and an
/// optional predicate asked at every node on the way in.
/// </summary>
/// <remarks>
/// The predicate is what makes the walk more than a leaf enumeration. GROUP BY
/// containment is decided sub-expression by sub-expression — real licenses
/// <c>SELECT a + 1</c> against <c>GROUP BY a + 1</c> and refuses a bare
/// <c>SELECT a</c> against the same clause — so the check has to be able to
/// stop descending the moment a node matches a grouping expression. Every
/// composite expression recurses through
/// <see cref="Expression.VisitColumnReferences(ColumnReferenceVisitor)"/>
/// rather than calling its child's <c>Core</c> directly, which is what puts
/// the predicate in front of each node.
/// </remarks>
internal sealed class ColumnReferenceVisitor(Action<MultiPartName> onReference, Func<Expression, bool>? coversSubtree)
{
    public readonly Action<MultiPartName> OnReference = onReference;

    /// <summary>
    /// Asked at each node before its children are walked; a true answer leaves
    /// the whole subtree unvisited. Null for the plain enumeration, which every
    /// caller but the GROUP BY containment check uses.
    /// </summary>
    public readonly Func<Expression, bool>? CoversSubtree = coversSubtree;
}
