using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// UPDATE through a view carrying an <c>INSTEAD OF UPDATE</c> trigger. The
    /// write never reaches a base table, so nothing about the view's
    /// updatability matters: the view is read, the rows its <c>WHERE</c> picks
    /// are <c>DELETED</c>, those rows with the <c>SET</c> list applied are
    /// <c>INSERTED</c>, and the trigger runs over both (probed 2026-09-27
    /// against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets a view real can't write through at all — aggregates,
    /// <c>DISTINCT</c>, a set operation, a join — take an UPDATE, and it is
    /// also the path an updatable view takes, since both the <c>WHERE</c> and
    /// the <c>SET</c> list may name a derived column there and each
    /// pseudo-table row carries every view column as the view computes it. A
    /// column the <c>SET</c> list leaves alone keeps its old value in
    /// <c>INSERTED</c>: a derived column isn't recomputed from the new values.
    /// </para>
    /// <para>
    /// The trigger fires even when no row qualifies. <c>OUTPUT</c> may name
    /// <c>DELETED</c> but not <c>INSERTED</c> (Msg 404, one per column), and
    /// its <c>INTO</c> rows land before the trigger body runs. A <c>FROM</c>
    /// clause naming the view alone — <paramref name="from"/>, read ahead of the
    /// <c>SET</c> list — picks the rows as the <c>WHERE</c> does, aliased or
    /// not, while one joining the view to another source is <strong>Msg
    /// 414</strong> (probed 2026-10-01 against SQL Server 2025). A positioned UPDATE
    /// (<c>WHERE CURRENT OF</c>) through an updatable view keeps the base-row
    /// path it had.
    /// </para>
    /// </remarks>
    private static SimulatedStatementOutcome ExecuteInsteadOfViewUpdate(
        ParserContext context,
        MultiPartName targetName,
        View view,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        Selection.DmlTopLimit? top,
        bool serializableHint,
        Selection.PreParsedFrom? from = null)
    {
        var batch = context.Batch;
        var statementView = view;
        var level = InsteadOfLevel(batch, ref view, ref targetName, TriggerActions.Update);
        var columns = ViewColumnsFor(batch, view, targetName);
        var collation = batch.CurrentDatabase.Collation;

        var assignments = new List<(int Ordinal, Expression Expr)>(rawAssignments.Count);
        var updatedOrdinals = new List<int>(rawAssignments.Count);
        foreach (var (columnName, expr) in rawAssignments)
        {
            if (columnName is null)
            {
                assignments.Add((-1, expr));
                continue;
            }
            var ordinal = level is null
                ? Array.FindIndex(columns, column => collation.Equals(column.Name, columnName))
                : level.ViewOrdinalOf(collation, columnName);
            if (ordinal < 0)
                throw SimulatedSqlException.InvalidColumnName(columnName);
            assignments.Add((ordinal, expr));
            updatedOrdinals.Add(ordinal);
        }

        var typeResolver = from is null ? Selection.ViewOutputColumnTypeResolver(batch.CurrentDatabase, statementView) : Selection.ColumnTypeResolverFor([.. from.Sources]);
        foreach (var (_, expr) in rawAssignments)
            UnresolvedCollation.RequireAssignable(expr.GetSqlType(batch, typeResolver));

        var shapeTable = view.BaseTable ?? ViewShapedTable(batch, view, columns);
        var output = TryParseOutputClauseForMutation(
            context, shapeTable, allowInserted: true, allowDeleted: true, new ViewOutputShape(columns, read: null, insertedRefused: static _ => true));
        RejectClientOutputOnTriggeredTarget(batch, view, TriggerActions.Update, targetName.ToString(), output is { HasTarget: false });

        // A FROM was read ahead of the SET list's consumers; an UPDATE naming
        // the view with one arrives through the joined-target path.
        if (from is not null)
            context.RestoreCheckpoint(from.After);

        if (IsWhereCurrentOf(context))
        {
            return view.BaseTable is { } baseTable && output is null
                ? ExecuteUpdateAgainstTable(context, targetName, baseTable, rawAssignments, output: null, top, serializableHint, view)
                : throw new NotSupportedException($"A positioned UPDATE through '{view.Name}', whose INSTEAD OF UPDATE trigger takes the write, isn't modeled with an OUTPUT clause or over a view with no single base table.");
        }

        var where = ParseInsteadOfViewWhere(context, typeResolver);
        CheckUpdatePermissions(context, targetName, shapeTable, view, rawAssignments, where);
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var deletedRows = new List<SqlValue[]>();
        var insertedRows = new List<SqlValue[]>();
        var statementColumns = level?.Columns ?? columns;
        SqlValue[] current = [];
        SqlValue Resolve(MultiPartName name)
        {
            var ordinal = Array.FindIndex(statementColumns, column => collation.Equals(column.Name, name.Leaf));
            return ordinal < 0 ? throw SimulatedSqlException.InvalidColumnName(name) : current[ordinal];
        }
        var runtime = new RuntimeContext(Resolve, batch);

        foreach (var (row, levelRow) in DmlTopIsZero(top, batch) ? [] : ReadInsteadOfRows(batch, view, columns, level))
        {
            current = levelRow;
            if (where is not null && where.Run(runtime) != true)
                continue;

            batch.BumpRowStamp();
            // Every variable is assigned first, against the old row, and only
            // then the columns — the order ComputeUpdatedRow follows.
            foreach (var (_, expr) in assignments)
            {
                if (expr is AssignmentExpression variableAssignment)
                    _ = variableAssignment.Run(runtime);
            }
            var updated = (SqlValue[])row.Clone();
            foreach (var (ordinal, expr) in assignments)
            {
                if (ordinal >= 0)
                    updated[ordinal] = CoerceForInsert(expr is AssignmentExpression { Slot: var assigned } ? assigned.Value : expr.Run(runtime), columns[ordinal]);
            }
            deletedRows.Add(row);
            insertedRows.Add(updated);
        }

        ApplyDmlTopCap(top, deletedRows, batch);
        if (insertedRows.Count > deletedRows.Count)
            insertedRows.RemoveRange(deletedRows.Count, insertedRows.Count - deletedRows.Count);

        if (output is not null)
        {
            for (var i = 0; i < deletedRows.Count; i++)
                _ = output.ProjectRow(batch, insertedValues: insertedRows[i], deletedValues: deletedRows[i]);
        }

        context.Connection.LastStatementRowCount = deletedRows.Count;
        _ = batch.Connection.Simulation.TryFireInsteadOfTrigger(
            batch, view, TriggerActions.Update, columns, insertedRows, deletedRows,
            affectedRowCount: deletedRows.Count, updatedColumnOrdinals: updatedOrdinals);
        return new SimulatedNonQuery(deletedRows.Count);
    }

    /// <summary>
    /// DELETE through a view carrying an <c>INSTEAD OF DELETE</c> trigger — the
    /// counterpart of <see cref="ExecuteInsteadOfViewUpdate"/>: the view is
    /// read, the rows its <c>WHERE</c> picks (which may name a derived column)
    /// are <c>DELETED</c>, and the trigger runs over them, whether or not the
    /// view is one real could delete through (probed 2026-09-27 against SQL
    /// Server 2025). <c>OUTPUT DELETED</c> reads the view's rows, its
    /// <c>INTO</c> rows landing before the body runs. A <c>FROM</c> clause
    /// naming the view alone is <paramref name="from"/>, as for the
    /// <c>UPDATE</c>.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteInsteadOfViewDelete(
        ParserContext context,
        MultiPartName targetName,
        View view,
        Selection.DmlTopLimit? top,
        bool serializableHint,
        Selection.PreParsedFrom? from = null)
    {
        var batch = context.Batch;
        var statementView = view;
        var level = InsteadOfLevel(batch, ref view, ref targetName, TriggerActions.Delete);
        var columns = ViewColumnsFor(batch, view, targetName);
        var collation = batch.CurrentDatabase.Collation;

        var shapeTable = view.BaseTable ?? ViewShapedTable(batch, view, columns);
        var output = TryParseOutputClauseForMutation(
            context, shapeTable, allowInserted: false, allowDeleted: true, new ViewOutputShape(columns, read: null, insertedRefused: null));
        RejectClientOutputOnTriggeredTarget(batch, view, TriggerActions.Delete, targetName.ToString(), output is { HasTarget: false });

        if (from is not null)
            context.RestoreCheckpoint(from.After);
        else if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            throw new NotSupportedException($"Multi-source DELETE through a view ('{view.Schema.Name}.{view.Name}') isn't modeled — target the underlying table directly.");

        if (IsWhereCurrentOf(context))
        {
            return view.BaseTable is { } baseTable && output is null
                ? ExecuteDeleteAgainstTable(context, targetName, baseTable, output: null, top, serializableHint, view)
                : throw new NotSupportedException($"A positioned DELETE through '{view.Name}', whose INSTEAD OF DELETE trigger takes the write, isn't modeled with an OUTPUT clause or over a view with no single base table.");
        }

        var where = ParseInsteadOfViewWhere(context, from is null ? Selection.ViewOutputColumnTypeResolver(batch.CurrentDatabase, statementView) : Selection.ColumnTypeResolverFor([.. from.Sources]));
        if (!batch.IsSkipping
            && PermissionEnforcement.SecurableFor(batch, targetName, view) is { } securable
            && PermissionEnforcement.Applies(batch, batch.DatabaseFor(securable)))
        {
            if (where is not null)
                PermissionEnforcement.CheckSchemaObject(batch, "SELECT", securable);
            PermissionEnforcement.CheckSchemaObject(batch, "DELETE", securable);
        }
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var deletedRows = new List<SqlValue[]>();
        var statementColumns = level?.Columns ?? columns;
        SqlValue[] current = [];
        SqlValue Resolve(MultiPartName name)
        {
            var ordinal = Array.FindIndex(statementColumns, column => collation.Equals(column.Name, name.Leaf));
            return ordinal < 0 ? throw SimulatedSqlException.InvalidColumnName(name) : current[ordinal];
        }
        var runtime = new RuntimeContext(Resolve, batch);
        foreach (var (row, levelRow) in DmlTopIsZero(top, batch) ? [] : ReadInsteadOfRows(batch, view, columns, level))
        {
            current = levelRow;
            if (where is null || where.Run(runtime) == true)
                deletedRows.Add(row);
        }
        ApplyDmlTopCap(top, deletedRows, batch);

        if (output is not null)
        {
            foreach (var row in deletedRows)
                _ = output.ProjectRow(batch, insertedValues: null, deletedValues: row);
        }

        context.Connection.LastStatementRowCount = deletedRows.Count;
        _ = batch.Connection.Simulation.TryFireInsteadOfTrigger(
            batch, view, TriggerActions.Delete, columns, insertedRows: null, deletedRows,
            affectedRowCount: deletedRows.Count);
        return new SimulatedNonQuery(deletedRows.Count);
    }

    /// <summary>Whether the cursor sits on <c>WHERE CURRENT OF</c>, left where it was either way.</summary>
    private static bool IsWhereCurrentOf(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Where })
            return false;
        var checkpoint = context.SaveCheckpoint();
        var positioned = context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Current };
        context.RestoreCheckpoint(checkpoint);
        return positioned;
    }

    /// <summary>An optional <c>WHERE</c> over a view's output columns, bound as it parses.</summary>
    private static BooleanExpression? ParseInsteadOfViewWhere(ParserContext context, Func<MultiPartName, SqlType> typeResolver)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Where })
            return null;
        context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Where);
        context.MoveNextRequired();
        return Selection.ParseAndBindPredicate(context, typeResolver);
    }

    /// <summary>
    /// A view, CTE or derived table written through whose single source is a
    /// view carrying an <c>INSTEAD OF</c> trigger for the action — directly,
    /// or through further such levels: real hands that trigger the view's
    /// rows the level shows, every filter on the way applied, as a write
    /// naming the view would (probed 2026-10-01 against SQL Server 2025,
    /// <c>UPDATE d … FROM (SELECT * FROM vi WHERE id = 1) d</c>,
    /// <c>WITH c AS (SELECT * FROM vi) DELETE c</c>, an <c>INSERT</c> through
    /// such a CTE and an <c>UPDATE</c> through a stored view over <c>vi</c>;
    /// probed 2026-10-06 two stored views above it).
    /// </summary>
    private sealed class InsteadOfLevelView(HeapColumn[] columns, HeapColumn[] sourceColumns, Expression[] projections, BooleanExpression[] excluders, string writtenName, bool isDerivedTable, InsteadOfLevelView? below)
    {
        /// <summary>The level's columns, which the statement's <c>WHERE</c>, <c>SET</c> list and <c>INSERT</c> column list name.</summary>
        public readonly HeapColumn[] Columns = columns;

        /// <summary>The columns of what the level reads: the level below's, or the trigger view's.</summary>
        private readonly HeapColumn[] sourceColumns = sourceColumns;
        private readonly Expression[] projections = projections;
        private readonly BooleanExpression[] excluders = excluders;
        private readonly string writtenName = writtenName;
        private readonly bool isDerivedTable = isDerivedTable;

        /// <summary>The level this one reads, null when it reads the trigger view.</summary>
        private readonly InsteadOfLevelView? below = below;

        /// <summary>
        /// The trigger view's column a level column passes through bare; -1
        /// when the level has no such column, and a derived one refused with
        /// Msg 4406 (4421 through a derived table) naming the level as written.
        /// </summary>
        public int ViewOrdinalOf(Collation collation, string levelColumn)
        {
            var i = Array.FindIndex(this.Columns, column => collation.Equals(column.Name, levelColumn));
            if (i < 0)
                return -1;
            if (UnwrapDirectRef(this.projections[i]) is not { ReferencedName: var name }
                || Array.FindIndex(this.sourceColumns, column => collation.Equals(column.Name, name.Leaf)) is not (var ordinal and >= 0))
            {
                throw SimulatedSqlException.ViewDmlTouchesDerivedField(this.writtenName, this.isDerivedTable);
            }
            return this.below is { } lower ? lower.ViewOrdinalOf(collation, this.sourceColumns[ordinal].Name) : ordinal;
        }

        /// <summary>The level's row over a trigger view row, or null when a filter on the way hides it.</summary>
        public SqlValue[]? RowOver(SqlValue[] viewRow, BatchContext batch)
        {
            var sourceRow = viewRow;
            if (this.below is { } lower)
            {
                if (lower.RowOver(viewRow, batch) is not { } lowerRow)
                    return null;
                sourceRow = lowerRow;
            }
            var collation = batch.CurrentDatabase.Collation;
            var columns = this.sourceColumns;
            var runtime = new RuntimeContext(name =>
                Array.FindIndex(columns, column => collation.Equals(column.Name, name.Leaf)) is var ordinal and >= 0
                    ? sourceRow[ordinal]
                    : throw SimulatedSqlException.InvalidColumnName(name), batch);
            foreach (var excluder in this.excluders)
            {
                if (excluder.Run(runtime) != true)
                    return null;
            }
            var row = new SqlValue[this.projections.Length];
            for (var i = 0; i < row.Length; i++)
                row[i] = this.projections[i].Run(runtime);
            return row;
        }
    }

    /// <summary>
    /// The view a level — a view, CTE or derived table — reads as its single
    /// source, or null.
    /// </summary>
    private static View? LevelSourceView(View level) =>
        level.UnstoredBody is { UpdatabilityProfile.Sources: [var source] } ? source.UpdatableView() : level.UpstreamView;

    /// <summary>
    /// The stored view carrying an <c>INSTEAD OF</c> trigger for
    /// <paramref name="action"/> that <paramref name="level"/> reads through
    /// its chain of single sources, or null.
    /// </summary>
    private static View? InsteadOfTriggerViewUnder(BatchContext batch, View level, TriggerActions action)
    {
        for (var (current, depth) = (level, 0); depth < SimulatedDbConnection.MaxNestingLevel && LevelSourceView(current) is { } next; (current, depth) = (next, depth + 1))
        {
            if (next.UnstoredBody is null && HasInsteadOfTrigger(batch, next, action))
                return next;
        }
        return null;
    }

    /// <summary>
    /// When <paramref name="view"/> is a level <see cref="InsteadOfTriggerViewUnder"/>
    /// finds a trigger view under, turns the write onto that view —
    /// <paramref name="view"/> and <paramref name="targetName"/> become its —
    /// and answers the level the statement names; null otherwise.
    /// </summary>
    private static InsteadOfLevelView? InsteadOfLevel(BatchContext batch, ref View view, ref MultiPartName targetName, TriggerActions action)
    {
        if (InsteadOfTriggerViewUnder(batch, view, action) is not { } triggerView)
            return null;
        var levels = new List<View>();
        for (var level = view; !ReferenceEquals(level, triggerView); level = LevelSourceView(level)!)
            levels.Add(level);
        var written = view.UnstoredBody is null ? targetName.ToString() : view.Name;
        targetName = new MultiPartName(triggerView.Schema.Name).WithAddedPart(triggerView.Name);
        InsteadOfLevelView? built = null;
        var sourceColumns = ViewColumnsFor(batch, triggerView, targetName);
        for (var i = levels.Count - 1; i >= 0; i--)
        {
            var level = levels[i];
            var profile = (level.UnstoredBody ?? batch.Connection.Simulation.ParseViewBodyPlan(batch, level)).UpdatabilityProfile!;
            built = new InsteadOfLevelView(level.OutputColumns, sourceColumns, profile.Projections, profile.Excluders, i == 0 ? written : level.Name, level.IsDerivedTable, built);
            sourceColumns = level.OutputColumns;
        }
        view = triggerView;
        return built;
    }

    /// <summary>
    /// Every row the trigger view yields, each beside the row the statement
    /// reads it as: itself, or through <paramref name="level"/>, whose filter
    /// drops the rows it hides.
    /// </summary>
    private static List<(SqlValue[] Row, SqlValue[] LevelRow)> ReadInsteadOfRows(BatchContext batch, View view, HeapColumn[] columns, InsteadOfLevelView? level)
    {
        var rows = new List<(SqlValue[] Row, SqlValue[] LevelRow)>();
        foreach (var row in ReadViewRows(batch, view, columns))
        {
            if (level is null)
                rows.Add((row, row));
            else if (level.RowOver(row, batch) is { } levelRow)
                rows.Add((row, levelRow));
        }
        return rows;
    }

    /// <summary>Every row the view yields, decoded to its columns.</summary>
    private static List<SqlValue[]> ReadViewRows(BatchContext batch, View view, HeapColumn[] columns)
    {
        var schema = new SqlType[columns.Length];
        for (var i = 0; i < columns.Length; i++)
            schema[i] = columns[i].Type;
        var rows = new List<SqlValue[]>();
        foreach (var bytes in Selection.ForView(view, columns).Execute(batch).RowBytes)
            rows.Add(RowDecoder.DecodeRow(schema.AsSpan(), bytes));
        return rows;
    }

    /// <summary>
    /// A table standing in for a view with no single base table where the
    /// OUTPUT parser wants one: it carries the view's name and columns and is
    /// never written.
    /// </summary>
    private static HeapTable ViewShapedTable(BatchContext batch, View view, HeapColumn[] columns) =>
        new(view.Name, columns, objectId: 0, createDate: batch.CurrentStatement.UtcNow, isTableVariable: true);
}
