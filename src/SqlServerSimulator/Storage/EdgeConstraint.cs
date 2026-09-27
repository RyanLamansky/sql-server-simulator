namespace SqlServerSimulator.Storage;

/// <summary>
/// An edge table's <c>CONNECTION (A TO B, …)</c> constraint: every edge row
/// must join a pair of node tables one of its clauses lists, and both nodes
/// must exist — a delete of a node an edge still reaches is refused, or
/// cascades to the edge under <c>ON DELETE CASCADE</c> (probed 2026-09-27
/// against SQL Server 2025). Lists as a <c>sys.objects</c> <c>EC</c> row and in
/// <c>sys.edge_constraints</c> / <c>sys.edge_constraint_clauses</c>.
/// </summary>
internal sealed class EdgeConstraint(string name, int objectId, (HeapTable From, HeapTable To)[] clauses, bool cascadeOnDelete, bool isSystemNamed, DateTime createDate)
{
    public string Name = name;

    public readonly int ObjectId = objectId;

    /// <summary>The node-table pairs, in the order written — <c>clause_number</c> is the 1-based position.</summary>
    public readonly (HeapTable From, HeapTable To)[] Clauses = clauses;

    public readonly bool CascadeOnDelete = cascadeOnDelete;

    public readonly bool IsSystemNamed = isSystemNamed;

    public readonly DateTime CreateDate = createDate;

    public DateTime ModifyDate = createDate;

    /// <summary>Added <c>WITH NOCHECK</c>, so the existing edges were never checked: <c>sys.edge_constraints.is_not_trusted</c>.</summary>
    public bool IsNotTrusted;

    /// <summary>Whether <paramref name="table"/> is an endpoint of any clause.</summary>
    public bool References(HeapTable table)
    {
        foreach (var (from, to) in this.Clauses)
        {
            if (ReferenceEquals(from, table) || ReferenceEquals(to, table))
                return true;
        }
        return false;
    }

    /// <summary>Whether some clause joins <paramref name="fromObjectId"/> to <paramref name="toObjectId"/>.</summary>
    public bool Admits(int fromObjectId, int toObjectId)
    {
        foreach (var (from, to) in this.Clauses)
        {
            if (from.ObjectId == fromObjectId && to.ObjectId == toObjectId)
                return true;
        }
        return false;
    }
}
