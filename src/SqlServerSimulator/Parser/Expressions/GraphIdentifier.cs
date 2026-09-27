using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>Which identifier a <see cref="GraphIdentifier"/> renders.</summary>
internal enum GraphIdentifierKind : byte
{
    Node,
    Edge,
    From,
    To,
}

/// <summary>
/// The expression behind a node or edge table's pseudo-columns: <c>$node_id</c>
/// / <c>$edge_id</c> render the row's own table and hidden <c>graph_id</c>,
/// <c>$from_id</c> / <c>$to_id</c> the node table and id an edge's hidden pair
/// stores. The table's name is read when the row is, so a renamed table's rows
/// render the new name (probed 2026-09-27 against SQL Server 2025).
/// </summary>
internal sealed class GraphIdentifier(GraphIdentifierKind kind, string idColumn, string? objectIdColumn, NVarcharSqlType type) : Expression
{
    private readonly NVarcharSqlType type = type;

    public readonly GraphIdentifierKind Kind = kind;

    private readonly Reference id = new(idColumn);

    private readonly Reference? objectId = objectIdColumn is null ? null : new Reference(objectIdColumn);

    /// <summary>
    /// The table whose column this is; set once the table exists, since the
    /// columns are built before it.
    /// </summary>
    public HeapTable? Owner;

    /// <summary>
    /// The last node table an edge endpoint resolved to, keyed by its object id,
    /// so a scan over an edge doesn't search the database once per row.
    /// </summary>
    private HeapTable? lastEndpoint;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var type = this.type;
        var idValue = this.id.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(type);
        var database = this.Owner!.OwningDatabase ?? runtime.Batch.CurrentDatabase;
        HeapTable? table;
        if (this.objectId is null)
        {
            table = this.Owner;
        }
        else
        {
            var objectIdValue = this.objectId.Run(runtime);
            if (objectIdValue.IsNull)
                return SqlValue.Null(type);
            var wanted = objectIdValue.CoerceTo(SqlType.Int32).AsInt32;
            table = this.lastEndpoint;
            if (table is null || table.ObjectId != wanted)
                this.lastEndpoint = table = GraphColumns.FindByObjectId(database, wanted);
            else if (GraphColumns.SchemaOf(database, table) is not { } schema || !schema.HeapTables.TryGetValue(table.Name, out var current) || !ReferenceEquals(current, table))
                this.lastEndpoint = table = null; // dropped since: the endpoint reads NULL
        }
        return table is not null && GraphColumns.RenderFor(database, table, idValue.CoerceTo(SqlType.BigInt).AsInt64) is { } text
            ? SqlValue.FromNVarchar(type, text)
            : SqlValue.Null(type);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.type;

    internal override string DebugDisplay() => $"GRAPH_IDENTIFIER({this.Kind})";

    internal override void Describe(NodeShape shape) => shape.Local(this.Kind).Child(this.id).Child(this.objectId);
}
