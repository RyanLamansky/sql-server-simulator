using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Node and edge tables: the AS NODE | AS EDGE clause, CONNECTION constraints,
// graph-id allocation and the pseudo-column writes. See docs/claude/graph.md.
partial class Simulation
{
    /// <summary>An edge constraint as written, kept until the table it lands on exists.</summary>
    internal sealed class PendingEdgeConstraint(string? name, List<(MultiPartName From, MultiPartName To)> clauses, bool cascadeOnDelete)
    {
        public readonly string? Name = name;
        public readonly List<(MultiPartName From, MultiPartName To)> Clauses = clauses;
        public readonly bool CascadeOnDelete = cascadeOnDelete;
    }

    /// <summary>
    /// Whether the column list the cursor opens (on its <c>(</c>) is followed by
    /// <c>AS NODE</c> or <c>AS EDGE</c>. Looked up before the list parses, since
    /// the internal columns lead the declared ones and every ordinal the list
    /// records has to count them.
    /// </summary>
    private static GraphTableKind PeekGraphTableKind(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            var depth = 0;
            do
            {
                switch (context.Token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        break;
                    case Operator { Character: ')' }:
                        if (--depth == 0)
                        {
                            return context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.As } && context.MoveNext()
                                ? GraphKeyword(context.Token)
                                : GraphTableKind.None;
                        }
                        break;
                }
            } while (context.MoveNext());
            return GraphTableKind.None;
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>The graph keyword after the <c>AS</c> the cursor sits on, without moving.</summary>
    private static GraphTableKind PeekGraphKeyword(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var kind = context.MoveNext() ? GraphKeyword(context.Token) : GraphTableKind.None;
        context.RestoreCheckpoint(checkpoint);
        return kind;
    }

    private static GraphTableKind GraphKeyword(Token? token) => token switch
    {
        UnquotedString { Value: var word } when word.Equals("NODE", StringComparison.OrdinalIgnoreCase) => GraphTableKind.Node,
        UnquotedString { Value: var word } when word.Equals("EDGE", StringComparison.OrdinalIgnoreCase) => GraphTableKind.Edge,
        _ => GraphTableKind.None,
    };

    /// <summary>
    /// Consumes <c>AS NODE</c> / <c>AS EDGE</c>, entered on <c>AS</c>, leaving the
    /// cursor on what follows. A node table needs a column list, so the
    /// list-less form accepts only <c>EDGE</c> (Msg 102 near <c>node</c>
    /// otherwise, probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static GraphTableKind ConsumeGraphTableClause(ParserContext context, bool hasColumnList)
    {
        var kind = GraphKeyword(context.GetNextRequired());
        if (kind == GraphTableKind.None || (kind == GraphTableKind.Node && !hasColumnList))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return kind;
    }

    /// <summary>
    /// Parses <c>CONNECTION (a TO b [, …]) [ON DELETE {CASCADE | NO ACTION}]</c>,
    /// entered on <c>CONNECTION</c>, leaving the cursor on the comma or
    /// closing paren after it. Real's grammar has no <c>SET NULL</c> /
    /// <c>SET DEFAULT</c> and no <c>ON UPDATE</c> here (Msg 156).
    /// </summary>
    private static PendingEdgeConstraint ParseEdgeConstraint(ParserContext context, string? name)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var clauses = new List<(MultiPartName From, MultiPartName To)>();
        do
        {
            context.MoveNextRequired();
            if (context.Token is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var from = BatchContext.ParseObjectName(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.To })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var to = BatchContext.ParseObjectName(context);
            clauses.Add((from, to));
            context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var cascade = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Delete })
                throw context.Token is ReservedKeyword onKeyword ? SimulatedSqlException.SyntaxErrorNearKeyword(onKeyword) : SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.GetNextRequired())
            {
                case ReservedKeyword { Keyword: Keyword.Cascade }:
                    cascade = true;
                    break;
                case UnquotedString { Value: var no } when no.Equals("NO", StringComparison.OrdinalIgnoreCase):
                    if (context.GetNextRequired() is not UnquotedString { Value: var action } || !action.Equals("ACTION", StringComparison.OrdinalIgnoreCase))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case ReservedKeyword actionKeyword:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(actionKeyword);
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }
        return new PendingEdgeConstraint(name, clauses, cascade);
    }

    /// <summary>
    /// Whether the cursor sits on a table-level <c>CONNECTION (</c> element
    /// rather than a column that happens to be named <c>connection</c>.
    /// </summary>
    private static bool IsEdgeConstraintAhead(ParserContext context)
    {
        if (context.Token is not UnquotedString { Value: var word } || !word.Equals("CONNECTION", StringComparison.OrdinalIgnoreCase))
            return false;
        var checkpoint = context.SaveCheckpoint();
        var isConstraint = context.MoveNext() && context.Token is Operator { Character: '(' };
        context.RestoreCheckpoint(checkpoint);
        return isConstraint;
    }

    /// <summary>
    /// Binds the <c>CONNECTION</c> constraints a <c>CREATE TABLE</c> or
    /// <c>ALTER TABLE … ADD</c> wrote onto <paramref name="table"/>, raising
    /// real's refusals, each followed by Msg 1750: <b>13930</b> on a table that
    /// isn't an edge table, <b>13931</b> for a table that doesn't exist and
    /// <b>13933</b> for one that isn't a node table (probed 2026-09-27 against
    /// SQL Server 2025).
    /// </summary>
    internal static List<EdgeConstraint> ResolveEdgeConstraints(ParserContext context, HeapTable table, List<PendingEdgeConstraint> pending)
    {
        var resolved = new List<EdgeConstraint>(pending.Count);
        for (var i = 0; i < pending.Count; i++)
        {
            var declaration = pending[i];
            var name = declaration.Name ?? AutoEdgeConstraintName(table.Name, table.EdgeConstraints.Count + i);
            if (table.GraphKind != GraphTableKind.Edge)
                throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.EdgeConstraintOnNonEdgeTable(table.Name), state: 0);
            var clauses = new (HeapTable From, HeapTable To)[declaration.Clauses.Count];
            for (var c = 0; c < clauses.Length; c++)
            {
                var (from, to) = declaration.Clauses[c];
                clauses[c] = (ResolveEdgeEndpoint(context, name, from), ResolveEdgeEndpoint(context, name, to));
            }
            resolved.Add(new EdgeConstraint(name, context.CurrentDatabase.AllocateObjectId(), clauses, declaration.CascadeOnDelete, declaration.Name is null, context.Batch.CurrentStatement.UtcNow));
        }
        return resolved;
    }

    private static HeapTable ResolveEdgeEndpoint(ParserContext context, string constraintName, MultiPartName name) =>
        !context.Batch.TryResolveTable(name, out var table)
            ? throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.EdgeConstraintTableNotFound(constraintName, name.Leaf))
            : table.GraphKind != GraphTableKind.Node
                ? throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.EdgeConstraintMustReferenceNodes())
                : table;

    /// <summary>
    /// Real's <c>EC__&lt;table8&gt;__&lt;8hex&gt;</c> auto-name shape; the hex
    /// is a deterministic hash rather than real's object-id-derived digits.
    /// </summary>
    private static string AutoEdgeConstraintName(string tableName, int declarationIndex)
    {
        var h = Fnv1a32.Initial;
        h.MixTableSeed(tableName);
        h.Mix((byte)'E');
        h.Mix((byte)declarationIndex);
        return FormatAutoConstraintName("EC__", tableName, null, h.Value);
    }

    /// <summary>
    /// The unique index real keeps over <c>graph_id</c>, as one more of the
    /// declaration's inline indexes — the last, so its object id comes first
    /// among the nonclustered ones and it takes index id 2 (probed 2026-09-27
    /// against SQL Server 2025).
    /// </summary>
    private static PendingInlineIndex GraphUniqueIndex(HeapColumn graphId, int keysBefore) =>
        new(
            "GRAPH_UNIQUE_INDEX_" + graphId.Name[(graphId.Name.LastIndexOf('_') + 1)..],
            isUnique: true,
            isClustered: false,
            [(graphId.Name, false)],
            [],
            filter: null,
            filterDefinition: null,
            new IndexOptions(ignoreDupKey: false, fillFactor: null, padIndex: null))
        {
            KeysBefore = keysBefore,
        };

    /// <summary>Points each pseudo-column's expression at the table that now owns it.</summary>
    private static void AttachGraphColumns(HeapTable table)
    {
        foreach (var column in table.Columns)
        {
            if (column.Computed is GraphIdentifier identifier)
                identifier.Owner = table;
        }
    }

    /// <summary>
    /// Settles a node or edge row's internal columns from what the INSERT wrote
    /// into its pseudo-columns. An explicit <c>$node_id</c> / <c>$edge_id</c>
    /// must name this table (Msg 13921 otherwise, NULL included) and supplies
    /// the row's graph id, raising the table's counter past it; an unwritten one
    /// takes the next id. A <c>$from_id</c> / <c>$to_id</c> that doesn't read as
    /// a node of an existing node table leaves its hidden pair NULL, which the
    /// NOT NULL check then reports as Msg 515 on <c>from_obj_id</c> /
    /// <c>to_obj_id</c> (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    internal static void SettleGraphColumns(HeapTable table, SqlValue[] rowValues, HeapColumn[] written, BatchContext batch)
    {
        var database = batch.DatabaseFor(table);
        var columns = table.Columns;
        var graphIdOrdinal = -1;
        var selfWritten = false;
        for (var i = 0; i < columns.Length; i++)
        {
            switch (columns[i].GraphKind)
            {
                case GraphColumnKind.GraphId:
                    graphIdOrdinal = i;
                    break;
                case GraphColumnKind.GraphIdComputed when Array.IndexOf(written, columns[i]) >= 0:
                    selfWritten = true;
                    var isEdge = table.GraphKind == GraphTableKind.Edge;
                    if (rowValues[i].IsNull
                        || !GraphColumns.TryParseWritten(rowValues[i].CoerceTo(SqlType.NVarchar).AsString, database, isEdge, out var named, out var explicitId)
                        || !ReferenceEquals(named, table))
                    {
                        throw SimulatedSqlException.GraphPseudoColumnJsonMalformed(isEdge ? GraphColumns.EdgeId : GraphColumns.NodeId);
                    }
                    rowValues[graphIdOrdinal] = SqlValue.FromInt64(explicitId);
                    long seen;
                    while ((seen = Interlocked.Read(ref table.NextGraphId)) <= explicitId
                        && Interlocked.CompareExchange(ref table.NextGraphId, explicitId + 1, seen) != seen)
                    {
                    }
                    break;
                case GraphColumnKind.FromIdComputed or GraphColumnKind.ToIdComputed:
                    // The pair precedes its pseudo-column: obj_id at i - 2, id at i - 1.
                    var value = rowValues[i];
                    if (!value.IsNull && GraphColumns.TryParseWritten(value.CoerceTo(SqlType.NVarchar).AsString, database, edge: false, out var node, out var nodeId))
                    {
                        rowValues[i - 2] = SqlValue.FromInt32(node.ObjectId);
                        rowValues[i - 1] = SqlValue.FromInt64(nodeId);
                    }
                    else
                    {
                        rowValues[i - 2] = SqlValue.Null(SqlType.Int32);
                        rowValues[i - 1] = SqlValue.Null(SqlType.BigInt);
                    }
                    break;
            }
        }
        if (!selfWritten)
        {
            rowValues[graphIdOrdinal] = SqlValue.FromInt64(Interlocked.Increment(ref table.NextGraphId) - 1);
        }
    }

    /// <summary>
    /// Checks a new edge row against each of its table's edge constraints: the
    /// row's pair of node tables must be one a clause admits, and both nodes
    /// must exist — Msg 547 naming the constraint otherwise (probed 2026-09-27
    /// against SQL Server 2025).
    /// </summary>
    internal static void EnforceEdgeConstraints(HeapTable table, SqlValue[] row, ParserContext context, string verb)
    {
        if (table.EdgeConstraints.Count == 0)
            return;
        var database = context.Batch.DatabaseFor(table);
        var fromObject = row[GraphColumns.OrdinalOf(table, GraphColumnKind.FromObjId)];
        var fromId = row[GraphColumns.OrdinalOf(table, GraphColumnKind.FromId)];
        var toObject = row[GraphColumns.OrdinalOf(table, GraphColumnKind.ToObjId)];
        var toId = row[GraphColumns.OrdinalOf(table, GraphColumnKind.ToId)];
        if (fromObject.IsNull || fromId.IsNull || toObject.IsNull || toId.IsNull)
            return;
        var (fromObjectId, toObjectId) = (fromObject.CoerceTo(SqlType.Int32).AsInt32, toObject.CoerceTo(SqlType.Int32).AsInt32);
        foreach (var constraint in table.EdgeConstraints)
        {
            if (constraint.IsDisabled)
                continue;
            if (!constraint.Admits(fromObjectId, toObjectId)
                || !NodeExists(database, fromObjectId, fromId)
                || !NodeExists(database, toObjectId, toId))
            {
                throw SimulatedSqlException.EdgeConstraintConflict(verb, constraint.Name, database.Name, QualifiedEdgeName(database, table));
            }
        }
    }

    private static string QualifiedEdgeName(Database database, HeapTable table) =>
        $"{GraphColumns.SchemaOf(database, table)?.Name ?? Database.DefaultSchemaName}.{table.Name}";

    private static bool NodeExists(Database database, int objectId, SqlValue graphId)
    {
        if (GraphColumns.FindByObjectId(database, objectId) is not { GraphKind: GraphTableKind.Node } node)
            return false;
        var storage = node.StorageOrdinals[GraphColumns.OrdinalOf(node, GraphColumnKind.GraphId)];
        return HeapSeekCache.For(node.Heap).AnyRowMatches(node.Heap, node.StoredColumns, [storage], [SqlType.BigInt], new SqlValueKey([graphId.CoerceTo(SqlType.BigInt)]));
    }

    /// <summary>The edge constraints across <paramref name="database"/> with a clause naming <paramref name="node"/>.</summary>
    internal static List<(HeapTable Edge, EdgeConstraint Constraint)> EdgeConstraintsReferencing(Database database, HeapTable node)
    {
        var found = new List<(HeapTable, EdgeConstraint)>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                foreach (var constraint in table.EdgeConstraints)
                {
                    if (constraint.References(node))
                        found.Add((table, constraint));
                }
            }
        }
        return found;
    }

    /// <summary>
    /// After a DELETE removed <paramref name="deletedRows"/> from a node table,
    /// finds the edges each edge constraint over the table still has reaching
    /// them: under <c>ON DELETE CASCADE</c> those edges are deleted, otherwise
    /// the statement fails with Msg 547 naming the constraint as an EDGE
    /// REFERENCE (probed 2026-09-27 against SQL Server 2025). An edge table
    /// without a constraint keeps its now-dangling edges, as real's does.
    /// </summary>
    internal static void EnforceEdgeConstraintsOnNodeDelete(HeapTable node, List<SqlValue[]> deletedRows, ParserContext context)
    {
        var database = context.Batch.DatabaseFor(node);
        var referencing = EdgeConstraintsReferencing(database, node);
        if (referencing.Count == 0 || deletedRows.Count == 0)
            return;
        var graphIdOrdinal = GraphColumns.OrdinalOf(node, GraphColumnKind.GraphId);
        var deletedIds = new HashSet<long>();
        foreach (var row in deletedRows)
        {
            if (!row[graphIdOrdinal].IsNull)
                _ = deletedIds.Add(row[graphIdOrdinal].CoerceTo(SqlType.BigInt).AsInt64);
        }
        foreach (var (edge, constraint) in referencing)
        {
            // A disabled constraint neither refuses nor cascades; it still
            // keeps the node table from being truncated or dropped.
            if (constraint.IsDisabled)
                continue;
            var reaching = EdgesReaching(edge, node.ObjectId, deletedIds);
            if (reaching.Count == 0)
                continue;
            if (!constraint.CascadeOnDelete)
                throw SimulatedSqlException.EdgeReferenceConflict("DELETE", constraint.Name, database.Name, QualifiedEdgeName(database, edge));
            // Unlike a foreign key's cascade, this one fires no DELETE trigger
            // on the edge table (probed 2026-09-27 against SQL Server 2025).
            var undoLog = context.Batch.CurrentUndoLog;
            foreach (var (pageIndex, slotIndex, full) in reaching)
            {
                edge.ChangeTracking?.RecordRow(context.Batch, edge, full, ChangeTrackingOperation.Delete);
                edge.Heap.DeleteAt(pageIndex, slotIndex, undoLog, ReclaimSuperseded(edge, context));
            }
        }
    }

    private static List<(int PageIndex, int SlotIndex, SqlValue[] Full)> EdgesReaching(HeapTable edge, int nodeObjectId, HashSet<long> nodeIds)
    {
        var (fromObjOrdinal, fromOrdinal, toObjOrdinal, toOrdinal) = (
            GraphColumns.OrdinalOf(edge, GraphColumnKind.FromObjId),
            GraphColumns.OrdinalOf(edge, GraphColumnKind.FromId),
            GraphColumns.OrdinalOf(edge, GraphColumnKind.ToObjId),
            GraphColumns.OrdinalOf(edge, GraphColumnKind.ToId));
        var reaching = new List<(int, int, SqlValue[])>();
        foreach (var (pageIndex, slotIndex, bytes) in edge.Heap.EnumerateRowsWithAddress())
        {
            var full = DecodeFullRow(edge, bytes);
            if (Reaches(full[fromObjOrdinal], full[fromOrdinal]) || Reaches(full[toObjOrdinal], full[toOrdinal]))
                reaching.Add((pageIndex, slotIndex, full));
        }
        return reaching;

        bool Reaches(SqlValue objectId, SqlValue id) =>
            !objectId.IsNull && !id.IsNull
            && objectId.CoerceTo(SqlType.Int32).AsInt32 == nodeObjectId
            && nodeIds.Contains(id.CoerceTo(SqlType.BigInt).AsInt64);
    }
}
