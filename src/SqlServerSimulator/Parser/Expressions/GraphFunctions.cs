using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>NODE_ID_FROM_PARTS(object_id, graph_id)</c> /
/// <c>EDGE_ID_FROM_PARTS(object_id, graph_id)</c>: the identifier a node or
/// edge table's row would carry, as <c>nvarchar(1000)</c> — NULL when either
/// part is NULL or the object isn't a node (edge) table of the current
/// database; the graph id needn't exist. Both parts take the integer types
/// only: a <c>bit</c>, <c>decimal</c> (an unsuffixed literal past
/// <c>int</c>'s range included), string or <c>sql_variant</c> is Msg 8116
/// (probed 2026-09-27 against SQL Server 2025).
/// </summary>
internal sealed class GraphIdFromParts : Expression
{
    private readonly bool edge;
    private readonly Expression objectId;
    private readonly Expression graphId;

    public GraphIdFromParts(ParserContext context, bool edge)
    {
        this.edge = edge;
        this.objectId = Parse(context);
        context.MoveNextRequired();
        this.graphId = Parse(context);
    }

    private string FunctionName => this.edge ? "edge_id_from_parts" : "node_id_from_parts";

    public override SqlValue Run(RuntimeContext runtime)
    {
        var type = ResultType(runtime.Batch);
        var objectIdValue = this.objectId.Run(runtime);
        var graphIdValue = this.graphId.Run(runtime);
        if (objectIdValue.IsNull || graphIdValue.IsNull)
            return SqlValue.Null(type);
        var database = runtime.Batch.CurrentDatabase;
        return GraphColumns.FindByObjectId(database, ScalarArguments.CoerceToInt(objectIdValue)) is { } table
            && table.GraphKind == (this.edge ? GraphTableKind.Edge : GraphTableKind.Node)
            && GraphColumns.RenderFor(database, table, graphIdValue.CoerceTo(SqlType.BigInt).AsInt64) is { } text
                ? SqlValue.FromNVarchar(type, text)
                : SqlValue.Null(type);
    }

    private static NVarcharSqlType ResultType(BatchContext batch) =>
        NVarcharSqlType.Get(1000, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        RequireInteger(this.objectId, 1);
        RequireInteger(this.graphId, 2);
        return ResultType(batch);

        void RequireInteger(Expression argument, int index)
        {
            if (IsUntypedNullLiteral(argument))
                return;
            var type = argument.GetSqlType(batch, resolveColumnType);
            if (type.Category != SqlTypeCategory.Integer || type == SqlType.Bit)
                throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(type, argument), index, this.FunctionName);
        }
    }

    internal override string DebugDisplay() => $"{this.FunctionName.ToUpperInvariant()}({this.objectId.DebugDisplay()}, {this.graphId.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.edge).Child(this.objectId).Child(this.graphId);
}

/// <summary>Which part of an identifier a <see cref="GraphIdPart"/> reads.</summary>
internal enum GraphIdPartKind : byte
{
    ObjectIdFromNode,
    GraphIdFromNode,
    ObjectIdFromEdge,
    GraphIdFromEdge,
}

/// <summary>
/// <c>OBJECT_ID_FROM_NODE_ID</c> / <c>GRAPH_ID_FROM_NODE_ID</c> /
/// <c>OBJECT_ID_FROM_EDGE_ID</c> / <c>GRAPH_ID_FROM_EDGE_ID</c>: the
/// <c>int</c> object id or <c>bigint</c> graph id an identifier names, NULL
/// for anything that doesn't read as one of a node (edge) table of the current
/// database — the reader is the strict one
/// <see cref="GraphColumns.TryParseStrict"/> describes. The argument takes the
/// character types only: an integer, <c>varbinary</c>, <c>xml</c> or
/// <c>json</c> is Msg 8116 (probed 2026-09-27 against SQL Server 2025).
/// </summary>
internal sealed class GraphIdPart(ParserContext context, GraphIdPartKind kind) : Expression
{
    private readonly Expression identifier = Parse(context);

    private readonly GraphIdPartKind kind = kind;

    private bool ReadsObjectId => this.kind is GraphIdPartKind.ObjectIdFromNode or GraphIdPartKind.ObjectIdFromEdge;

    private string FunctionName => this.kind switch
    {
        GraphIdPartKind.ObjectIdFromNode => "object_id_from_node_id",
        GraphIdPartKind.GraphIdFromNode => "graph_id_from_node_id",
        GraphIdPartKind.ObjectIdFromEdge => "object_id_from_edge_id",
        _ => "graph_id_from_edge_id",
    };

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.identifier.Run(runtime);
        var edge = this.kind is GraphIdPartKind.ObjectIdFromEdge or GraphIdPartKind.GraphIdFromEdge;
        if (value.IsNull || !GraphColumns.TryParseStrict(value.CoerceTo(SqlType.NVarchar).AsString, runtime.Batch.CurrentDatabase, edge, out var table, out var id))
            return SqlValue.Null(this.ReadsObjectId ? SqlType.Int32 : SqlType.BigInt);
        return this.ReadsObjectId ? SqlValue.FromInt32(table.ObjectId) : SqlValue.FromInt64(id);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (!IsUntypedNullLiteral(this.identifier)
            && this.identifier.GetSqlType(batch, resolveColumnType) is var type
            && (type.Category != SqlTypeCategory.String || type is JsonSqlType or XmlSqlType))
        {
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(type, this.identifier), 1, this.FunctionName);
        }
        return this.ReadsObjectId ? SqlType.Int32 : SqlType.BigInt;
    }

    internal override string DebugDisplay() => $"{this.FunctionName.ToUpperInvariant()}({this.identifier.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.kind).Child(this.identifier);
}
