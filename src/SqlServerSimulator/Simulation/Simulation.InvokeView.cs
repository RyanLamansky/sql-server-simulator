using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Executes a view body and yields its row bytes. Synthesizes a
    /// <see cref="SimulatedDbCommand"/> over <see cref="View.BodyText"/>,
    /// builds a child <see cref="BatchContext"/> sharing the caller's
    /// connection (so the body sees the same schemas / tables / temp tables
    /// / current transaction), and dispatches the body's SELECT through the
    /// regular <see cref="Selection.Parse"/> / <see cref="Selection.Execute"/>
    /// pair. Recursion (a view referencing another view, or a view
    /// referencing a TVF / scalar UDF that re-enters via FROM) counts
    /// against the shared <see cref="SimulatedDbConnection.NestingLevel"/>
    /// — exceeding 32 raises Msg 217.
    /// </summary>
    /// <param name="outerBatch">The batch whose statement referenced the view.</param>
    /// <param name="view">The view to execute.</param>
    /// <param name="columnCount">
    /// How many of the body's columns the reference reads — the count
    /// <see cref="BindViewColumns"/> settled. A body that has grown past it
    /// since CREATE has its extra trailing columns dropped from every row.
    /// </param>
    /// <param name="pushedPredicates">
    /// WHERE conjunct templates the referencing statement pushed into this
    /// reference, applied to the body plan once it is parsed (a view's own
    /// projection isn't known before that). Null for an ordinary reference; a
    /// body whose shape can't take them keeps running unchanged.
    /// </param>
    internal IEnumerable<byte[]> InvokeView(
        BatchContext outerBatch, View view, int columnCount, List<BooleanExpression>? pushedPredicates = null) =>
        outerBatch.Connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel
            ? throw SimulatedSqlException.MaximumNestingLevelExceeded()
            : InvokeViewCore(outerBatch, view, columnCount, pushedPredicates);

    /// <summary>
    /// The columns a reference to <paramref name="view"/> reads, settled the
    /// way real binds one: the body is re-bound against the objects as they
    /// stand now, and its output maps onto the names recorded at CREATE <em>by
    /// position</em>. The names and their count stay the recorded ones; each
    /// column's type is whatever the re-bound body now projects there.
    /// </summary>
    /// <remarks>
    /// Only a <c>SELECT *</c> body over a table changed since CREATE can drift,
    /// and real's positional mapping is observable there (probed 2026-09-23):
    /// a column added to the first table of <c>SELECT * FROM t CROSS JOIN u</c>
    /// reads under <c>u</c>'s recorded column name; a dropped-and-recreated
    /// base table serves its new first column under the old name, typed as
    /// the new column; a body left with fewer columns than recorded names is
    /// Msg 4502. <c>sp_refreshview</c> is what re-records the names on real.
    /// A body that no longer binds returns the recorded columns unchanged, so
    /// its own error surfaces at execution as it always has.
    /// </remarks>
    internal HeapColumn[] BindViewColumns(BatchContext outerBatch, View view)
    {
        if (this.TryParseViewBodyPlan(outerBatch, view) is not { } plan)
            return view.OutputColumns;

        var recorded = view.OutputColumns;
        var bound = plan.Schema;
        if (bound.Length < recorded.Length)
            throw SimulatedSqlException.ViewHasMoreColumnNamesThanColumns(view.Name);

        var nullability = plan.ColumnNullability;
        HeapColumn[]? rebound = null;
        for (var i = 0; i < recorded.Length; i++)
        {
            if (ReferenceEquals(bound[i], recorded[i].Type))
                continue;
            rebound ??= [.. recorded];
            var nullable = nullability is null || i >= nullability.Length || nullability[i];
            rebound[i] = new HeapColumn(recorded[i].Name, bound[i], maxLength: null, nullable: nullable);
        }
        return rebound ?? recorded;
    }

    /// <summary>
    /// Parses a view's stored body and returns its plan without executing it —
    /// the seam cursor planning uses to look through a view down to the base
    /// tables it reads. Mirrors <see cref="InvokeViewCore"/>'s child-batch
    /// setup (the CREATE-time <c>QUOTED_IDENTIFIER</c> swap, the shared nesting
    /// cap) but stops at parse.
    /// </summary>
    /// <remarks>
    /// Returns null rather than propagating when the body won't parse or bind:
    /// the caller then leaves the cursor STATIC, and the body's own error
    /// surfaces at OPEN through <see cref="InvokeViewCore"/> exactly as it did
    /// before the cursor asked. Declaring a cursor is not the place to report
    /// a broken view.
    /// </remarks>
    internal Selection? TryParseViewBodyPlan(BatchContext outerBatch, View view)
    {
        try
        {
            return ParseViewBodyPlan(outerBatch, view, releaseStatementSchemaLocks: true);
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The propagating form of <see cref="TryParseViewBodyPlan"/>, used by
    /// DML through a join view: the statement is about to write through the
    /// body, so a body that won't parse or bind is the statement's own error
    /// rather than something to fall back from. Statement schema locks stay
    /// held for the rest of the statement.
    /// </summary>
    internal Selection ParseViewBodyPlan(BatchContext outerBatch, View view) =>
        ParseViewBodyPlan(outerBatch, view, releaseStatementSchemaLocks: false);

    private Selection ParseViewBodyPlan(BatchContext outerBatch, View view, bool releaseStatementSchemaLocks)
    {
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // view.BodyText is the view's pre-validated stored body, not external input
        bodyCommand.CommandText = view.BodyText;
#pragma warning restore CA2100
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var innerBatch = new BatchContext(bodyCommand, variables, new UdfFrame(SqlType.Int32)) { SuppressDiagnosticsResolution = true };
        innerBatch.AdoptStatementFreezeFrom(outerBatch);
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        connection.QuotedIdentifiers = view.UsesQuotedIdentifier;
        var savedAnsiNulls = connection.AnsiNulls;
        connection.AnsiNulls = view.UsesAnsiNulls;
        connection.NestingLevel++;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextRequired();
            return ParseBodyQuery(parser, position: QueryPosition.Inlined);
        }
        finally
        {
            connection.NestingLevel--;
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
            if (releaseStatementSchemaLocks)
                innerBatch.ReleaseStatementSchemaLocks();
        }
    }

    private IEnumerable<byte[]> InvokeViewCore(
        BatchContext outerBatch, View view, int columnCount, List<BooleanExpression>? pushedPredicates)
    {
        var connection = outerBatch.Connection;
        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // view.BodyText is the view's pre-validated stored body, not external input
        bodyCommand.CommandText = view.BodyText;
#pragma warning restore CA2100

        // Views have no parameters. The empty variables dict + dummy UdfFrame
        // keep the BatchContext signature uniform with scalar UDF / inline
        // TVF invocation — the body's RETURN <value> form is parse-time-
        // rejected here because UdfFrame is set, but that's harmless since
        // a view body is a plain SELECT with no RETURN.
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var dummyFrame = new UdfFrame(SqlType.Int32);
        // The body parses under the QUOTED_IDENTIFIER captured at CREATE, not
        // the caller's. Swapping the session flag (rather than seeding the
        // child parser) is what carries it to everything else that reads the
        // connection — dynamic SQL, the plan-cache key, the Msg 1934 gates.
        // Restored in the finally below; see docs/claude/grammar.md.
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        connection.QuotedIdentifiers = view.UsesQuotedIdentifier;
        var savedAnsiNulls = connection.AnsiNulls;
        connection.AnsiNulls = view.UsesAnsiNulls;
        // Body errors attribute to the outer statement that referenced the view
        // (probe-confirmed: real reports the outer SELECT's line, no procedure).
        var innerBatch = new BatchContext(bodyCommand, variables, dummyFrame) { SuppressDiagnosticsResolution = true };
        // The body is part of the referencing statement, not a statement of its
        // own, so its current-time calls read that statement's freeze.
        innerBatch.AdoptStatementFreezeFrom(outerBatch);
        connection.NestingLevel++;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextRequired();
            var bodySelection = ParseBodyQuery(parser, position: QueryPosition.Inlined);
            // A view body is inlined into the referencing statement, so its
            // reads reach no ordinary check site — and every same-database one
            // is chained anyway. What isn't chained is a read into another
            // database: DB_CHAINING off breaks the chain at that boundary, so
            // the caller needs its own rights there (probe-confirmed).
            PermissionEnforcement.CheckCrossDatabaseReads(outerBatch, view.Schema.Database, bodySelection.ReferencedSecurables);
            // A referencing statement's pushed WHERE conjuncts land here, after
            // the body bound (so its own errors report as they always did) and
            // after the permission check (which reads the body as written). A
            // body whose shape can't take them declines and runs unchanged; the
            // conjuncts are still applied by the referencing statement either
            // way, so this only ever narrows what the body produces.
            var effective = pushedPredicates is null
                ? bodySelection
                : bodySelection.PredicatePushdown?.Invoke(pushedPredicates) ?? bodySelection;
            var resultSet = effective.Execute(innerBatch, outerResolver: null);
            if (resultSet.Schema.Length > columnCount)
            {
                // The body grew since CREATE; the reference reads only the
                // leading columns the view recorded names for.
                var leading = resultSet.Schema[..columnCount];
                foreach (var values in resultSet.RowValues)
                    yield return RowEncoder.EncodeRow(leading, values.AsSpan(0, columnCount));
            }
            else
            {
                foreach (var rowBytes in resultSet.RowBytes)
                    yield return rowBytes;
            }
        }
        finally
        {
            connection.NestingLevel--;
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
            // The body's Sch-S / IS holds are recorded against this inner
            // batch, which the dispatch loop never sees — so without this the
            // referencing statement's own release leaves them behind and they
            // outlive the connection entirely.
            innerBatch.ReleaseStatementSchemaLocks();
        }
    }
}
