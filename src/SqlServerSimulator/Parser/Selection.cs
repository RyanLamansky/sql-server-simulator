using System.Collections;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Manages the higher-level logic to convert a sequence of command tokens into tabular results.
/// </summary>
/// <remarks>
/// <para>
/// Parsing and execution are split: <see cref="Parse"/> captures the
/// projection / FROM (with JOINs) / WHERE / GROUP BY / HAVING / ORDER BY
/// into a frozen plan and returns it; <see cref="Execute"/> materializes
/// one <see cref="SimulatedSqlResultSet"/> per call. The split lets
/// correlated subqueries (EXISTS / IN(SELECT) / scalar) re-execute the
/// inner SELECT per outer row by passing a different <c>outerResolver</c>
/// each time. For the non-correlated and top-level cases,
/// <see cref="Execute"/> is called once with no outer resolver — the
/// deferred shape is invisible to those callers.
/// </para>
/// <para>
/// Multi-source FROM clauses (one or more JOINs) are represented as a
/// <see cref="FromSource"/>[] plus a parallel <see cref="JoinSpec"/>[]
/// (one shorter — the leftmost source has no join). The row stream
/// consumed by the projector is a sequence of <c>byte[]?[]</c> tuples
/// (one byte[] per source, null for an unmatched LEFT-JOIN slot).
/// Column resolution walks all sources via a qualifier-aware lookup;
/// unqualified collisions raise Msg 209.
/// </para>
/// <para>
/// Correlated lookup chains via the <c>outerResolver</c> argument: a
/// column reference that doesn't resolve in any local FROM source falls
/// through to the outer scope, which itself falls through to its outer,
/// and so on. Type resolution at parse time follows the same chain
/// through <see cref="ParserContext.OuterTypeResolver"/>.
/// </para>
/// <para>
/// This file holds the public surface and the parser-side logic
/// (Parse / ParseInner / FROM-source + JOIN parsing / WHERE / GROUP BY /
/// HAVING / ORDER BY / tableless-SELECT shortcut). The execution-side
/// helpers (row pipeline, projection paths, column resolution at
/// runtime) live in <c>Selection.Execution.cs</c> as the other half of
/// the same partial class.
/// </para>
/// </remarks>
internal sealed partial class Selection
{
    public readonly SqlType[] Schema;
    public readonly string[] ColumnNames;

    /// <summary>
    /// The exposed name of each FROM source in FROM order (alias, else the
    /// object name as written), captured for <c>FOR XML AUTO</c> /
    /// <c>FOR JSON AUTO</c>, which name each nesting level after its owning
    /// table. An empty array means the SELECT has no FROM clause at all
    /// (Msg 6800 / 13600); null means the shape carries no source binding
    /// (set-op chains), where AUTO falls back to the unmodeled rejection.
    /// Paired with <see cref="AutoColumnSource"/>.
    /// </summary>
    internal string?[]? AutoSourceNames;

    /// <summary>
    /// Per projection column, the index into <see cref="AutoSourceNames"/> of
    /// the FROM source it reads, or -1 when the column is an expression
    /// (SQL Server's "computed column", which joins the level of the table
    /// column that precedes it). Null exactly when
    /// <see cref="AutoSourceNames"/> is.
    /// </summary>
    internal int[]? AutoColumnSource;

    /// <summary>
    /// Per projection column, the index of the source column it reads within
    /// its <see cref="AutoColumnSource"/> entry's <see cref="FromSource"/>, or
    /// -1 for an expression. Paired with <see cref="AutoColumnSource"/> and
    /// null exactly when it is; read by <c>FOR XML AUTO</c>'s binary
    /// <c>dbobject</c> addressing, which writes base column names rather than
    /// select-list aliases.
    /// </summary>
    internal int[]? AutoColumnOrdinal;

    /// <summary>
    /// True when this plan internally bakes an ORDER BY clause into its
    /// row pipeline. Set-op chaining inspects this on the first branch:
    /// per SQL Server, a per-branch ORDER BY is illegal when a set
    /// operator follows (Msg 156), and the simulator rejects via
    /// <see cref="CombineSetOps"/>. Top-level ORDER BY (after a set-op
    /// chain) is applied by <see cref="ApplyTopLevelOrderBy"/> and also
    /// sets this flag on the wrapper.
    /// </summary>
    public readonly bool HasOrderBy;

    /// <summary>
    /// True when this plan baked in a <c>TOP</c> count, an <c>OFFSET</c>,
    /// or a <c>FETCH</c> at any layer. The CTE body parser pairs this with
    /// <see cref="HasOrderBy"/> to enforce SQL Server's Msg 1033 — a CTE
    /// body's <c>ORDER BY</c> requires a companion <c>TOP</c> / <c>OFFSET</c>.
    /// </summary>
    public readonly bool HasTopOrOffsetOrFetch;

    /// <summary>
    /// True when every projection element is an <see cref="AssignmentExpression"/>
    /// — i.e. <c>SELECT @v = expr [, @w = expr2 ...] [FROM ...]</c>. The
    /// dispatch in <c>Simulation.CreateResultSetsForCommand</c> drains the
    /// row sequence (running the per-row side effects of writing to slots)
    /// but yields a <see cref="SimulatedNonQuery"/> rather than a result
    /// set — matches SQL Server's behavior of suppressing the result-set
    /// envelope for SELECT-assign. Set-op / recursive-CTE / etc. paths
    /// default to false.
    /// </summary>
    public readonly bool IsAssignmentOnly;

    /// <summary>
    /// Target table name for a <c>SELECT … INTO target …</c> statement; null
    /// for a regular SELECT. Captured at parse time when an <c>INTO</c>
    /// clause appears between the projection list and FROM. Set-op chains
    /// propagate the first-branch INTO through <c>CombineSetOps</c>;
    /// a subsequent branch carrying its own INTO is rejected as a syntax
    /// error (real SQL Server allows INTO only on the first branch). The
    /// dispatch routes Selections with this set to the SELECT INTO handler
    /// rather than the regular execute path.
    /// </summary>
    public readonly MultiPartName? IntoTarget;

    /// <summary>
    /// Real tables / views / TVFs this query reads (including those in nested
    /// subqueries and derived tables), recorded at parse time so the
    /// execution-time SELECT permission check runs against the current
    /// principal. Principal-independent, so it rides the cached plan. Null when
    /// the query reads nothing checkable (constant SELECT, all-system-table).
    /// Set once by the outermost <see cref="ParseQueryExpression"/>.
    /// </summary>
    public List<ReferencedSecurable>? ReferencedSecurables;

    /// <summary>
    /// Per table / view <c>object_id</c>, the 1-based column ordinals this query
    /// reads — the input to the execution-time column-level SELECT check
    /// (Msg 230 / 229). Recorded at parse time (principal-independent, rides the
    /// cached plan) from the resolved column references across the projection,
    /// WHERE, JOIN ON, GROUP BY, HAVING, and ORDER BY of every (sub)query.
    /// An object present with an <em>empty</em> ordinal set is read without
    /// naming a column (<c>COUNT(*)</c> / <c>SELECT 1</c> / <c>EXISTS</c>), which
    /// real checks as requiring SELECT on every column. A source reached through
    /// a synonym is absent (a synonym takes no column grants, so it is checked
    /// object-grain). Null when the query reads no column-grantable object
    /// (constant SELECT / all-system-table). Set once by the outermost
    /// <see cref="ParseQueryExpression"/>.
    /// </summary>
    public Dictionary<int, ColumnReadTarget>? ReadColumnsByObject;

    /// <summary>
    /// Pre-computed destination schema (column names + types + nullability
    /// + identity flags) for a <c>SELECT INTO</c> statement; null when
    /// <see cref="IntoTarget"/> is null. Built during projection planning
    /// from the projection expressions and FROM sources, applying SQL
    /// Server's documented schema-inference rules (direct refs preserve
    /// source nullability + identity; expressions / aggregates / casts /
    /// COALESCE always nullable; ISNULL non-null when either arg is
    /// non-null; CASE non-null when every branch is non-null; string `+`
    /// non-null when both operands non-null; integer arithmetic always
    /// nullable due to overflow). The SELECT INTO handler reads this
    /// directly to create the destination heap table.
    /// </summary>
    public readonly HeapColumn[]? DestColumnSchema;

    /// <summary>
    /// Non-null when this Selection is shape-eligible to back DML through a
    /// view: no DISTINCT, no aggregates, no windows, no GROUP BY, no HAVING,
    /// no set-op chain. The <see cref="ViewUpdatabilityProfile"/> exposes the
    /// FROM sources and their joins, the projection expressions, and the
    /// WHERE excluders — enough for <see cref="View"/> to derive its
    /// base-column map from a single-source body and re-evaluate that body's
    /// WHERE against a base-table row at DML time, and enough for the
    /// join-view UPDATE path to fold a multi-source one per statement. Null
    /// for any other shape; the DML-through-view path inspects the null+
    /// <see cref="ViewUpdatabilityRejection"/> to surface
    /// <strong>Msg 4403</strong> / <strong>Msg 4406</strong> / <strong>Msg
    /// 4405</strong>.
    /// </summary>
    internal readonly ViewUpdatabilityProfile? UpdatabilityProfile;

    /// <summary>
    /// When <see cref="UpdatabilityProfile"/> is null, the reason — drives
    /// Msg 4403 (aggregates / DISTINCT / GROUP BY) vs Msg 4406 (derived
    /// projection) vs Msg 4405 (multi-base-table) at DML time. Always
    /// <see cref="ViewUpdatabilityRejection.None"/> when the profile is set.
    /// </summary>
    internal readonly ViewUpdatabilityRejection UpdatabilityRejection;

    /// <summary>
    /// The FROM shape an updatable cursor may navigate — every source either a
    /// direct base-table scan or a deferred body the cursor can follow down to
    /// base tables (derived table, CTE, APPLY right side, view), joined by
    /// kinds the cursor fold handles. Null forces STATIC outright; non-null is
    /// the input <see cref="TryBuildCursorPlan"/> resolves into a
    /// <see cref="CursorSourcePlan"/> at DECLARE CURSOR time, which is where a
    /// view body is parsed. Set post-construction by
    /// <see cref="BuildSqlProjection"/>.
    /// </summary>
    internal CursorShape? CursorShape;

    /// <summary>
    /// The SELECT's ORDER BY items, captured for the updatable-cursor
    /// enumeration path (<c>EnumerateForCursor</c>) so KEYSET / DYNAMIC
    /// cursors and positioned DML can order rows the same way a read would.
    /// Non-null only when <see cref="CursorShape"/> is set; empty when the
    /// cursor's SELECT has no ORDER BY. Set post-construction by
    /// <see cref="BuildSqlProjection"/>.
    /// </summary>
    internal List<OrderBySpec>? CursorOrderBy;

    /// <summary>
    /// Per-column nullability for result-set metadata, parallel to
    /// <see cref="Schema"/>; true = nullable. Null when unknown (joined /
    /// set-op / non-projection shapes), which consumers treat as
    /// all-nullable. Set post-construction by <c>BuildSqlProjection</c> for
    /// the single-source no-join shape — see
    /// <c>ComputeColumnNullability</c> for the inference rules and why
    /// DacFx bacpac export depends on this reaching the TDS COLMETADATA
    /// fNullable flag.
    /// </summary>
    internal bool[]? ColumnNullability;

    /// <summary>
    /// Per-column significant-digit count for projection columns that are
    /// non-negative integer literals (<c>0</c> for non-literal columns); null
    /// when no column is an integer literal. Lets set-op column-type unification
    /// size a literal as <c>numeric(digit_count, 0)</c> against a decimal branch
    /// (<c>SELECT 1 UNION SELECT 2.5</c> → <c>numeric(2, 1)</c>), and propagates
    /// through nested set-ops. Set post-construction by the projection builders
    /// and <see cref="CombineSetOps"/>.
    /// </summary>
    internal int[]? ColumnIntegerLiteralDigits;

    /// <summary>
    /// Per-column decimal-vs-numeric reported type name for projection columns; true =
    /// report the <c>numeric</c> type name rather than <c>decimal</c>, null
    /// when no decimal column is numeric-named. Flows to the result set's
    /// <see cref="SimulatedQueryResult.ColumnReportsNumeric"/> so the reader /
    /// wire type-name path reports it. Set post-construction by the projection
    /// builders and <see cref="CombineSetOps"/> — see
    /// <c>Expression.ResultReportsNumeric</c> for the propagation rule.
    /// </summary>
    internal bool[]? ColumnReportsNumeric;

    private readonly Func<BatchContext, Func<MultiPartName, SqlValue>?, IEnumerable<byte[]>>? rowSource;

    /// <summary>
    /// Fast-path projection producer for the FROM-bearing SELECT (line-205
    /// dispatch in <c>Selection.Execution.cs</c>): the row is already a
    /// <see cref="SqlValue"/> array, so the reader's cursor serves cells
    /// directly and skips the encode-then-re-decode round-trip a byte-row
    /// would force. Niche producers (set ops, TVFs, OPENJSON, views, …) stay
    /// on <see cref="rowSource"/>. Exactly one of the two is non-null.
    /// </summary>
    private readonly Func<BatchContext, Func<MultiPartName, SqlValue>?, IEnumerable<SqlValue[]>>? valueRowSource;

    private Selection(SqlType[] schema, string[] columnNames, bool hasOrderBy, bool hasTopOrOffsetOrFetch, Func<BatchContext, Func<MultiPartName, SqlValue>?, IEnumerable<byte[]>> rowSource, bool isAssignmentOnly = false, MultiPartName? intoTarget = null, HeapColumn[]? destColumnSchema = null, ViewUpdatabilityProfile? updatabilityProfile = null, ViewUpdatabilityRejection updatabilityRejection = ViewUpdatabilityRejection.UnsupportedShape)
    {
        this.Schema = schema;
        this.ColumnNames = columnNames;
        this.HasOrderBy = hasOrderBy;
        this.HasTopOrOffsetOrFetch = hasTopOrOffsetOrFetch;
        this.IsAssignmentOnly = isAssignmentOnly;
        this.rowSource = rowSource;
        this.IntoTarget = intoTarget;
        this.DestColumnSchema = destColumnSchema;
        this.UpdatabilityProfile = updatabilityProfile;
        this.UpdatabilityRejection = updatabilityProfile is null ? updatabilityRejection : ViewUpdatabilityRejection.None;
    }

    private Selection(SqlType[] schema, string[] columnNames, bool hasOrderBy, bool hasTopOrOffsetOrFetch, Func<BatchContext, Func<MultiPartName, SqlValue>?, IEnumerable<SqlValue[]>> valueRowSource, bool isAssignmentOnly = false, MultiPartName? intoTarget = null, HeapColumn[]? destColumnSchema = null, ViewUpdatabilityProfile? updatabilityProfile = null, ViewUpdatabilityRejection updatabilityRejection = ViewUpdatabilityRejection.UnsupportedShape)
    {
        this.Schema = schema;
        this.ColumnNames = columnNames;
        this.HasOrderBy = hasOrderBy;
        this.HasTopOrOffsetOrFetch = hasTopOrOffsetOrFetch;
        this.IsAssignmentOnly = isAssignmentOnly;
        this.valueRowSource = valueRowSource;
        this.IntoTarget = intoTarget;
        this.DestColumnSchema = destColumnSchema;
        this.UpdatabilityProfile = updatabilityProfile;
        this.UpdatabilityRejection = updatabilityProfile is null ? updatabilityRejection : ViewUpdatabilityRejection.None;
    }

    /// <summary>
    /// Wraps a <see cref="CatalogView"/>'s row generator + column schema as a
    /// <see cref="Selection"/> suitable for use as a <see cref="FromSource.LateralPlan"/>.
    /// Executing the resulting plan invokes the view's generator with the
    /// live <see cref="BatchContext"/>, encodes each row's
    /// <see cref="SqlValue"/> array via <c>RowEncoder.EncodeRow</c>, and
    /// streams the bytes. Re-executes on each call so changes made earlier
    /// in the same batch (CREATE TABLE, CREATE SCHEMA, DROP TABLE) appear
    /// immediately.
    /// </summary>
    internal static Selection ForCatalogView(CatalogView view, Database targetDatabase)
    {
        var (schema, columnNames) = CatalogViewShape(view);
        return new Selection(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (batch, _) =>
            {
                CatalogPushdownDiagnostics.Sink?.Add($"Scan({view.Name})");
                var gated = BuiltInResources.ApplyDmvGate(view, batch, view.RowGenerator(batch, targetDatabase));
                var rows = BuiltInResources.ApplyMetadataFilter(view, batch, targetDatabase, gated);
                return rows.Select(values => RowEncoder.EncodeRow(view.Columns, values));
            });
    }

    /// <summary>
    /// Predicate-pushdown variant of <see cref="ForCatalogView(CatalogView,Database)"/>:
    /// the WHERE equality <c>&lt;pushdownColumn&gt; = &lt;comparand&gt;</c> is
    /// evaluated once per execution (the comparand is row-independent, so a column
    /// resolver is never consulted) and handed to the view's
    /// <see cref="CatalogView.FilteredRowGenerator"/> so it enumerates only
    /// matching objects. The enclosing SELECT keeps applying the full WHERE as a
    /// residual filter, so this only narrows the generator's output — never the
    /// result. A NULL comparand yields no rows (<c>= NULL</c> is UNKNOWN for every
    /// candidate). The comparand's value is resolved per execution (variables /
    /// parameters differ between runs), keeping the compiled plan shareable across
    /// sessions.
    /// </summary>
    internal static Selection ForCatalogView(CatalogView view, Database targetDatabase, string pushdownColumn, Expression comparand)
    {
        var (schema, columnNames) = CatalogViewShape(view);
        var filteredGenerator = view.FilteredRowGenerator!;
        return new Selection(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (batch, _) =>
            {
                var value = comparand.Run(new RuntimeContext(
                    name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), batch));
                CatalogPushdownDiagnostics.Sink?.Add(
                    value.IsNull ? $"SeekEmpty({view.Name}.{pushdownColumn})" : $"Seek({view.Name}.{pushdownColumn})");
                var filter = new CatalogFilter(pushdownColumn, value);
                var gated = BuiltInResources.ApplyDmvGate(view, batch, filteredGenerator(batch, targetDatabase, filter));
                var rows = BuiltInResources.ApplyMetadataFilter(view, batch, targetDatabase, gated);
                return rows.Select(values => RowEncoder.EncodeRow(view.Columns, values));
            });
    }

    private static (SqlType[] Schema, string[] ColumnNames) CatalogViewShape(CatalogView view)
    {
        var schema = new SqlType[view.Columns.Length];
        var columnNames = new string[view.Columns.Length];
        for (var i = 0; i < view.Columns.Length; i++)
        {
            schema[i] = view.Columns[i].Type;
            columnNames[i] = view.Columns[i].Name;
        }
        return (schema, columnNames);
    }

    /// <summary>
    /// Wraps a table value constructor's rows (<c>(VALUES (…), (…)) alias(cols)</c>)
    /// as a <see cref="Selection"/> usable as a <see cref="FromSource.LateralPlan"/>.
    /// Each row's cell expressions are evaluated per <see cref="Execute"/>
    /// against the outer-row resolver — so a VALUES source under CROSS / OUTER
    /// APPLY can correlate to the left side (the SSMS server-properties shape)
    /// — coerced to the per-column promoted <paramref name="schema"/> type, and
    /// encoded. Riding the deferred lateral-plan seam is what gives VALUES its
    /// per-outer-row correlation for free, exactly like a derived-table SELECT.
    /// </summary>
    private static Selection ForValuesConstructor(SqlType[] schema, string[] columnNames, List<Expression[]> tuples) =>
        new(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (batch, outerResolver) => EnumerateValuesRows(schema, tuples, batch, outerResolver));

    private static IEnumerable<byte[]> EnumerateValuesRows(SqlType[] schema, List<Expression[]> tuples, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        SqlValue Resolve(MultiPartName name) =>
            outerResolver is not null ? outerResolver(name) : throw SimulatedSqlException.InvalidColumnName(name);
        var runtime = new RuntimeContext(Resolve, batch);
        foreach (var tuple in tuples)
        {
            var values = new SqlValue[schema.Length];
            for (var c = 0; c < schema.Length; c++)
            {
                var raw = tuple[c].Run(runtime);
                values[c] = raw.IsNull || raw.Type == schema[c] ? raw : raw.CoerceTo(schema[c]);
            }
            yield return RowEncoder.EncodeRow(schema, values);
        }
    }

    /// <summary>
    /// Materializes the SELECT against the given outer-row resolver
    /// (null for top-level / non-correlated scopes). Each call produces a
    /// fresh <see cref="SimulatedSqlResultSet"/>; the underlying row sequence
    /// is itself lazy or eager depending on whether DISTINCT / ORDER BY /
    /// aggregation force buffering. <paramref name="batch"/> is the
    /// executing <see cref="BatchContext"/> — threaded through so
    /// <see cref="Expression.Run(RuntimeContext)"/> calls inside the row
    /// generation can build a <see cref="RuntimeContext"/> with explicit
    /// per-batch / per-session / per-database access.
    /// </summary>
    public SimulatedSqlResultSet Execute(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver = null) =>
        this.valueRowSource is { } values
            ? new SimulatedSqlResultSet(this.Schema, this.ColumnNames, values(batch, outerResolver)) { ColumnNullability = this.ColumnNullability, ColumnReportsNumeric = this.ColumnReportsNumeric }
            : new SimulatedSqlResultSet(this.Schema, this.ColumnNames, this.rowSource!(batch, outerResolver)) { ColumnNullability = this.ColumnNullability, ColumnReportsNumeric = this.ColumnReportsNumeric };

    /// <summary>
    /// Creates a <see cref="Selection"/> from a series of tokens. Follows the
    /// lookahead contract documented on <see cref="ParserContext"/>: on
    /// return, <see cref="ParserContext.Token"/> is the first token not
    /// consumed by the SELECT (typically <c>;</c>, <c>)</c> for a derived
    /// table or subquery, or null at end of command).
    /// </summary>
    /// <param name="context">Manages the overall parsing state.</param>
    /// <param name="depth">The current depth of recursed selection, such as with derived tables. 0 for the top-level SELECT.</param>
    /// <param name="outerTypeResolver">Outer-scope column type resolver used during projection planning when this SELECT references an enclosing scope's columns. Null for the top-level / non-correlated case.</param>
    /// <returns>The prepared plan; call <see cref="Execute"/> to materialize results.</returns>
    /// <exception cref="SimulatedSqlException">A variety of messages are possible for various problems with the command.</exception>
    /// <exception cref="NotSupportedException">A condition was encountered that may be valid but can't currently be parsed.</exception>
    public static Selection Parse(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver = null)
    {
        // A subquery is never constant, and the predicate forms that hold one
        // without routing it through Expression.Parse (EXISTS, IN (SELECT …),
        // the quantified comparisons) would otherwise leave an enclosing
        // constant-fold frame believing every operand was a literal.
        context.FoldableArguments = false;
        return ParseQueryExpression(context, depth, outerTypeResolver);
    }

    /// <summary>
    /// Parses a full query expression: a chain of set-op-combined SELECT
    /// branches optionally followed by a top-level ORDER BY. Set-op
    /// precedence: <c>INTERSECT</c> binds tighter than <c>UNION</c> /
    /// <c>EXCEPT</c> (which are at the same level, left-to-right).
    /// </summary>
    private static Selection ParseQueryExpression(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        // The outermost query expression owns the securable sink; every nested
        // subquery / derived table appends to it, so the returned top-level
        // plan carries the flat set of everything the statement reads.
        var ownsSecurableSink = context.SecurableSink is null;
        if (ownsSecurableSink)
        {
            context.SecurableSink = [];
            context.ReadColumnSink = [];
            // Statement-scoped slot: a value left over from a statement that
            // failed for another reason (continue-on-error, TRY/CATCH) never
            // reaches this statement's flush below.
            context.PendingGroupByBindError = null;
        }

        // A set-op result column is an output column of the query expression,
        // so a branch pair that can't settle one collation reports Msg 451
        // there — unless the whole result feeds an assignment target (which
        // supplies the collation) or an EXISTS (whose projection is discarded).
        var sequenceDrawsBefore = context.SequenceDrawsParsed;
        var unwindowedSequenceDrawsBefore = context.UnwindowedSequenceDrawsParsed;
        var combined = ParseUnionExceptChain(
            context, depth, outerTypeResolver, !context.ProjectionDiscarded && !context.InInsertSourceSelect);

        // Msg 422 is settled against the shape of the whole statement, so the
        // bare-projection flag is read before the clauses below can consume
        // anything more (each of which real accepts as a use of the prefix).
        var bareProjectionStatement = context.LastQuerySpecIsBareProjection;

        // Top-level ORDER BY: applies to the combined result (post-set-op).
        // ORDER BY references within set-op chains use the first branch's
        // column names. Top-level OFFSET/FETCH (post-chain) attaches here
        // too; FETCH-without-OFFSET on a single SELECT is also caught here
        // when the cursor sits on FETCH after no ORDER BY was consumed.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            // The ORDER BY a set-op chain carries applies to the combined
            // result, so it earns real's Msg 11723 for any sequence draw in
            // any branch — settled here for the same reason the branch-level
            // check exists, the clause following everything it judges.
            if (context.UnwindowedSequenceDrawsParsed > unwindowedSequenceDrawsBefore)
                throw SimulatedSqlException.NextValueForNotAllowedWithOrderBy();
            var orderBy = new List<OrderBySpec>();
            ParseOrderByItems(context, orderBy);
            var topLevelTail = new FromClause();
            ConsumeOffsetFetch(context, topLevelTail);
            if (topLevelTail.OffsetExpression is not null && context.SequenceDrawsParsed > sequenceDrawsBefore)
                throw SimulatedSqlException.NextValueForNotAllowedWithRowLimit();
            combined = ApplyTopLevelOrderBy(combined, orderBy, topLevelTail.OffsetExpression, topLevelTail.FetchExpression);
            bareProjectionStatement = false;
        }

        // Trailing FOR JSON { PATH | AUTO } [, options]: wraps the combined
        // result in a single-column JSON-string serializer. Sits where FOR XML
        // / FOR BROWSE do (after ORDER BY / OFFSET-FETCH, before OPTION); a
        // non-JSON FOR clause is left in place for the downstream Msg 102.
        var beforeForClauses = combined;
        combined = ParseOptionalForJson(context, combined, depth);

        // Trailing FOR XML { RAW | AUTO | PATH } [, ELEMENTS …] [, ROOT …]:
        // wraps the result in a single-column xml serializer. Sits in the same
        // slot; a non-XML FOR clause is left in place for the downstream Msg 102.
        combined = ParseOptionalForXml(context, combined, depth);
        if (!ReferenceEquals(combined, beforeForClauses))
            bareProjectionStatement = false;

        // OPTION (hint [, …]) — statement-level hint clause. Parsed as a
        // closed-list per Selection.Hints.cs; MAXRECURSION applies to in-
        // scope recursive CTEs, everything else recognized is discarded
        // (the simulator has nothing to dispatch on a hint against).
        if (context.Token is ReservedKeyword { Keyword: Keyword.Option })
        {
            ParseOptionClause(context);
            bareProjectionStatement = false;
        }

        if (ownsSecurableSink)
        {
            // Msg 422 — a WITH prefix whose statement is a bare
            // `SELECT <expression list>`. Real's refusal is that narrow: the
            // same prefix over `SELECT 1 WHERE 1 = 1`, `SELECT 1 ORDER BY 1`,
            // `SELECT TOP 1 1`, `SELECT DISTINCT 1`, `SELECT 1 UNION SELECT 2`,
            // `SELECT (SELECT MAX(a) FROM t)`, `SELECT 1 FOR JSON PATH`,
            // `SELECT 1 OPTION (MAXDOP 1)` and every INSERT / UPDATE / DELETE /
            // MERGE / SELECT … INTO form is accepted, and only one CTE anywhere
            // in the prefix has to go unused for the bare shape to raise
            // (probed 2026-08-05). A bare projection can name no CTE — it has
            // no FROM and no subquery — so the shape settles the diagnostic on
            // its own.
            if (bareProjectionStatement && context.CtePrefixLeadsSelectStatement && context.CteBindings is { Count: > 0 })
                throw SimulatedSqlException.CteDefinedButNotUsed();

            if (context.SecurableSink is { Count: > 0 } sink)
                combined.ReferencedSecurables = sink;
            if (context.ReadColumnSink is { Count: > 0 } readColumns)
                combined.ReadColumnsByObject = readColumns;
            context.SecurableSink = null;
            context.ReadColumnSink = null;

            // The statement has parsed, so the GROUP BY clause's held binding
            // error is due — unless the cursor stopped on a value literal,
            // which a well-formed query never leaves behind. That is the
            // trailing-token syntax error the dispatcher raises next, and real
            // reports it ahead of any binding error in the same batch.
            if (context.PendingGroupByBindError is { } pending)
            {
                context.PendingGroupByBindError = null;
                throw context.Token is Numeric or Literal
                    ? SimulatedSqlException.SyntaxErrorNear(context)
                    : pending;
            }
        }

        return combined;
    }

    /// <summary>
    /// Lower-precedence set-op level: parses a chain of UNION /
    /// UNION ALL / EXCEPT operators left-to-right, with each operand
    /// parsed via <see cref="ParseIntersectChain"/> (which handles the
    /// higher-precedence INTERSECT operator). The first branch gets
    /// <c>allowOrderBy=true</c> so single-SELECT queries with ORDER BY
    /// retain the existing inside-the-projection behavior (which can
    /// reference non-projected source columns); subsequent branches use
    /// <c>allowOrderBy=false</c> and any post-chain ORDER BY is applied
    /// at the top level.
    /// </summary>
    private static Selection ParseUnionExceptChain(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool namesOwnCollation)
    {
        var savedRejection = context.NextValueForRejection;
        var sequenceDrawsBefore = context.SequenceDrawsParsed;
        try
        {
            return ParseUnionExceptChainCore(context, depth, outerTypeResolver, namesOwnCollation, sequenceDrawsBefore);
        }
        finally
        {
            context.NextValueForRejection = savedRejection;
        }
    }

    private static Selection ParseUnionExceptChainCore(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool namesOwnCollation, int sequenceDrawsBefore)
    {
        var left = ParseIntersectChain(context, depth, outerTypeResolver, isFirstBranch: true, namesOwnCollation);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Union or Keyword.Except } op)
        {
            RejectSequenceDrawUnderSetOperator(context, sequenceDrawsBefore);
            SetOpKind kind;
            if (op.Keyword == Keyword.Union)
            {
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.All })
                {
                    kind = SetOpKind.UnionAll;
                    context.MoveNextRequired();
                }
                else
                {
                    kind = SetOpKind.Union;
                }
            }
            else
            {
                kind = SetOpKind.Except;
                context.MoveNextRequired();
            }

            var right = ParseIntersectChain(context, depth, outerTypeResolver, isFirstBranch: false, namesOwnCollation);
            RecordSetOperationShape(context);
            left = CombineSetOps(left, right, kind, namesOwnCollation);
        }
        return left;
    }

    /// <summary>
    /// Raises real's Msg 11721 at the set operator itself when the branch
    /// already parsed drew from a sequence. The operator is read after that
    /// branch, so the refusal it earns can only be settled here; the branches
    /// that follow get the same refusal eagerly, since by then the operator is
    /// in hand. An <c>OVER</c> does not exempt a reference from this one
    /// (probe-confirmed) — only from the <c>ORDER BY</c> refusal.
    /// </summary>
    private static void RejectSequenceDrawUnderSetOperator(ParserContext context, int sequenceDrawsBefore)
    {
        if (context.SequenceDrawsParsed > sequenceDrawsBefore)
            throw SimulatedSqlException.NextValueForNotAllowedWithDedup();
        _ = context.EnterNextValueForScope(NextValueForScope.Deduplicating);
    }

    /// <summary>
    /// Notes a UNION / INTERSECT / EXCEPT for the indexed-view battery
    /// (Msg 10116) and for a function body's Msg 444 state, which real reports
    /// as 2 for a set-op chain even when no branch reads a table. Recorded at
    /// the two chain sites rather than inside <c>CombineSetOps</c>, which has no
    /// parser context.
    /// </summary>
    private static void RecordSetOperationShape(ParserContext context)
    {
        if (context.IndexedViewShapeCollector is { } shape)
            shape.HasSetOperation = true;

        // A combined result is no longer a bare projection however bare each
        // branch was, and the last branch's own parse is what set the flag.
        context.LastQuerySpecIsBareProjection = false;
        FunctionBodyShape.NoteRowsetRead(context);
    }

    /// <summary>
    /// Higher-precedence set-op level: parses a chain of INTERSECT
    /// operators left-to-right.
    /// </summary>
    internal static Selection ParseIntersectChain(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool isFirstBranch, bool namesOwnCollation)
    {
        var savedRejection = context.NextValueForRejection;
        var sequenceDrawsBefore = context.SequenceDrawsParsed;
        try
        {
            return ParseIntersectChainCore(context, depth, outerTypeResolver, isFirstBranch, namesOwnCollation, sequenceDrawsBefore);
        }
        finally
        {
            context.NextValueForRejection = savedRejection;
        }
    }

    private static Selection ParseIntersectChainCore(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool isFirstBranch, bool namesOwnCollation, int sequenceDrawsBefore)
    {
        var left = ParseSetOpBranch(context, depth, outerTypeResolver, allowOrderBy: isFirstBranch, namesOwnCollation);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Intersect })
        {
            RejectSequenceDrawUnderSetOperator(context, sequenceDrawsBefore);
            context.MoveNextRequired();
            var right = ParseSetOpBranch(context, depth, outerTypeResolver, allowOrderBy: false, namesOwnCollation);
            RecordSetOperationShape(context);
            left = CombineSetOps(left, right, SetOpKind.Intersect, namesOwnCollation);
        }
        return left;
    }

    /// <summary>
    /// Parses one branch of a set-op chain. A branch may be parenthesized, and
    /// the parentheses may wrap a whole nested chain rather than a single
    /// SELECT — `SELECT … UNION (SELECT … UNION SELECT …)` is what an ORM emits
    /// when it combines an already-combined queryset (probe-confirmed on
    /// SQL Server 2025, as is a parenthesized *first* branch).
    /// Without this the opening paren read as a scalar subquery, so the branch
    /// looked like a one-column select list and the chain failed the
    /// equal-expression-count check instead.
    /// </summary>
    private static Selection ParseSetOpBranch(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool allowOrderBy, bool namesOwnCollation)
    {
        if (context.Token is not Operator { Character: '(' })
            return ParseSingleSelectStatement(context, depth, outerTypeResolver, allowOrderBy);

        context.MoveNextRequired();
        var inner = ParseUnionExceptChain(context, depth, outerTypeResolver, namesOwnCollation);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return inner;
    }

    /// <summary>
    /// Parses a single SELECT statement (the leaf of a set-op chain).
    /// Each branch gets its own aggregate-collector scope so aggregates
    /// inside one branch don't leak into another.
    /// <paramref name="allowOrderBy"/> is true only for the very first
    /// branch parsed (or the entire query if no set-op follows) so that
    /// non-set-op queries like <c>SELECT name FROM t ORDER BY id</c>
    /// keep the existing branch-internal sort that can reference
    /// non-projected source columns; subsequent branches must defer
    /// ORDER BY to the top level.
    /// </summary>
    internal static Selection ParseSingleSelectStatement(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool allowOrderBy)
    {
        // Save / restore the parser's aggregate and window collectors so
        // each branch gets its own scope. Aggregates and window functions
        // parsed inside the projection / HAVING register into the
        // respective lists; the executor uses the populated lists to
        // switch into aggregate or windowed-projection mode.
        // An EXISTS body's projection is never materialized, so it doesn't have
        // to name an output collation. Claim the flag here — the SELECT that
        // consumes it is the one EXISTS wrapped — and leave it cleared so a
        // derived table or subquery nested inside the body names its own.
        var projectionDiscarded = context.ProjectionDiscarded;
        context.ProjectionDiscarded = false;
        var savedAggregateCollector = context.AggregateCollector;
        var savedWindowCollector = context.WindowCollector;
        // ParseInner installs this scope's FROM sources as the outer resolver
        // so the select list can bind against them; restoring it here keeps
        // the enclosing scope intact on every exit path, including throws.
        var savedOuterTypeResolver = context.OuterTypeResolver;
        // The full-text predicates and the spatial property form bind against
        // this scope's own sources; a nested query installs its own and the
        // enclosing one comes back here.
        var savedScopeSources = context.ScopeSources;
        // A nested query body owns its own name scope, so its column references
        // are not the enclosing FROM source's arguments — suspend the sibling
        // collector for its duration.
        var savedFromSourceColumnSink = context.FromSourceColumnSink;
        context.FromSourceColumnSink = null;
        // DISTINCT / TOP raise the branch's NEXT VALUE FOR refusal floor
        // inside ParseInner; the frame that installed it comes back here.
        var savedNextValueForRejection = context.NextValueForRejection;
        var aggregates = new List<AggregateExpression>();
        var windows = new List<WindowExpression>();
        var savedEnclosingAggregateCollector = context.EnclosingAggregateCollector;
        context.EnclosingAggregateCollector = savedAggregateCollector;
        context.AggregateCollector = aggregates;
        context.WindowCollector = windows;
        try
        {
            return ParseInner(context, depth, aggregates, windows, outerTypeResolver, allowOrderBy, projectionDiscarded);
        }
        finally
        {
            context.AggregateCollector = savedAggregateCollector;
            context.WindowCollector = savedWindowCollector;
            context.EnclosingAggregateCollector = savedEnclosingAggregateCollector;
            context.OuterTypeResolver = savedOuterTypeResolver;
            context.ScopeSources = savedScopeSources;
            context.FromSourceColumnSink = savedFromSourceColumnSink;
            context.NextValueForRejection = savedNextValueForRejection;
        }
    }

    /// <summary>
    /// Bundles the post-FROM clause state — WHERE excluders, GROUP BY keys,
    /// HAVING predicate, ORDER BY — so the recursive parse helpers can
    /// share one growing state record without lengthening every signature.
    /// </summary>
    private sealed class FromClause
    {
        public readonly List<BooleanExpression> Excluders = [];

        /// <summary>
        /// Each entry is one grouping set — the list of expressions whose
        /// distinct combinations bucket rows for that set's pass. Simple
        /// <c>GROUP BY a, b</c> produces a single entry <c>[a, b]</c>; ROLLUP,
        /// CUBE, GROUPING SETS, and mixed forms desugar to multiple entries
        /// via parse-time Cartesian product across all top-level GROUP BY
        /// items. Empty list = no GROUP BY (the implicit-empty-set rule —
        /// either no aggregates either, or one implicit group covering all
        /// rows). A single empty-array entry <c>[]</c> = the explicit
        /// <c>GROUPING SETS(())</c> form — one group, whole rowset.
        /// </summary>
        public readonly List<Expression[]> GroupingSets = [];

        /// <summary>
        /// Union of every expression that appears in any
        /// <see cref="GroupingSets"/> entry, in first-seen order. Used by
        /// GROUPING()/GROUPING_ID() to validate that their argument matches
        /// a GROUP BY column (Msg 8161 otherwise) and to discover the column
        /// to test in the current grouping set's "grouped-away" check.
        /// </summary>
        public readonly List<Expression> AllGroupingExpressions = [];

        public BooleanExpression? Having;
        public readonly List<OrderBySpec> OrderBy = [];

        /// <summary>
        /// The <c>OFFSET</c> count expression. Null when no OFFSET clause was
        /// present. Validated at parse time (type + non-negativity, Msg
        /// 10742) but resolved again per execution — the expression may carry
        /// parameters whose values differ between executions of one
        /// plan-cached SELECT.
        /// </summary>
        public Expression? OffsetExpression;

        /// <summary>
        /// The <c>FETCH NEXT</c> / <c>FETCH FIRST</c> count expression. Null
        /// when no FETCH clause was present (OFFSET-only is valid; FETCH-only
        /// is rejected at parse time via Msg 153). Validated at parse time
        /// (type + &gt; 0, Msg 10744) but resolved per execution, like
        /// <see cref="OffsetExpression"/>.
        /// </summary>
        public Expression? FetchExpression;

        /// <summary>
        /// A copy carrying <paramref name="extra"/> appended to
        /// <see cref="Excluders"/> — the shape the predicate pushdown hands to
        /// the aggregate projector, which reads its grouping / HAVING state from
        /// here. A copy rather than a mutation because the original belongs to
        /// the cached plan (see <c>docs/claude/plan-cache.md</c>); the appended
        /// conjuncts go <em>after</em> the body's own, so the body's WHERE still
        /// decides first for every row it excluded before.
        /// </summary>
        public FromClause WithExtraExcluders(List<BooleanExpression> extra)
        {
            var copy = new FromClause
            {
                Having = this.Having,
                OffsetExpression = this.OffsetExpression,
                FetchExpression = this.FetchExpression,
            };
            copy.Excluders.AddRange(this.Excluders);
            copy.Excluders.AddRange(extra);
            copy.GroupingSets.AddRange(this.GroupingSets);
            copy.AllGroupingExpressions.AddRange(this.AllGroupingExpressions);
            copy.OrderBy.AddRange(this.OrderBy);
            return copy;
        }
    }

    /// <summary>
    /// Which row-count-limit clause a count expression came from — each has
    /// its own range validation.
    /// </summary>
    private enum RowLimitKind
    {
        Top,
        Offset,
        Fetch,
    }

    /// <summary>
    /// Resolves a <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> count expression
    /// against the executing batch. Called once at parse time for immediate
    /// validation (mirroring real SQL Server's compile-time rejection of a
    /// bad literal) and again per execution inside the plan's row-source
    /// closure — the expression may carry parameters or variables, so a
    /// plan-cached SELECT must re-resolve rather than replay the parse-time
    /// value (EF's <c>Skip</c>/<c>Take</c> emit exactly this shape).
    /// </summary>
    private static int? ResolveRowCountLimit(Expression? expression, RowLimitKind kind, BatchContext batch)
    {
        if (expression is null)
            return null;
        var resolved = expression.Run(new RuntimeContext(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), batch));
        var count = ClampRowCount(resolved);
        return kind switch
        {
            RowLimitKind.Offset when count < 0 => throw SimulatedSqlException.OffsetMustNotBeNegative(),
            RowLimitKind.Fetch when count < 1 => throw SimulatedSqlException.FetchMustBeGreaterThanZero(),
            _ => count,
        };
    }

    /// <summary>
    /// The row count a <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> operand
    /// yields. Real accepts any integer-family value and any exact numeric at
    /// <b>scale 0</b> — an integer literal past int's range is
    /// <c>numeric(digit_count, 0)</c>, so <c>TOP (9999999999)</c> is an
    /// ordinary accepted row count — narrowing the operand to <c>bigint</c>
    /// (a 20-digit literal overflows there with Msg 8115 naming
    /// <c>bigint</c>). A fractional scale is the grammar's Msg 1060, as is
    /// any other family and NULL. The result clamps to <c>int</c>: no
    /// simulated row source reaches 2^31 rows, so a wider cap or offset is
    /// indistinguishable from the clamp.
    /// </summary>
    private static int ClampRowCount(SqlValue resolved)
    {
        if (resolved.IsNull || !(SqlType.IsIntegerCategory(resolved.Type) || resolved.Type is DecimalSqlType { scale: 0 }))
            throw SimulatedSqlException.TopFetchRequiresInteger();
        var wide = resolved.CoerceTo(SqlType.BigInt).AsInt64;
        return wide > int.MaxValue ? int.MaxValue
            : wide < int.MinValue ? int.MinValue
            : (int)wide;
    }

    /// <summary>
    /// Resolves a <c>TOP (n) PERCENT</c> value: numeric, coerced to float and
    /// validated to <c>[0, 100]</c> (Msg 1031; NULL → Msg 1014). Returns the
    /// percentage; the row cap (<c>ceil(count × pct / 100)</c>) is applied once
    /// the buffered rowcount is known. Mirrors <see cref="ResolveDmlTopCap"/>'s
    /// percent branch.
    /// </summary>
    private static double ResolveTopPercentValue(Expression expression, BatchContext batch)
    {
        var resolved = expression.Run(new RuntimeContext(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), batch));
        var pct = resolved.IsNull
            ? throw SimulatedSqlException.TopClauseInvalidValue()
            : resolved.CoerceTo(SqlType.Float).AsDouble;
        return pct is < 0 or > 100
            ? throw SimulatedSqlException.TopPercentOutOfRange()
            : pct;
    }

    /// <summary>
    /// The resolved SELECT <c>TOP</c> row cap: an integer count, a percentage
    /// (<see cref="Percent"/> non-null), and whether <c>WITH TIES</c> extends
    /// the cap to include rows tying the boundary row's ORDER BY key. Built
    /// per execution (the count/percent expression may carry variables) and
    /// applied by the buffered projection paths.
    /// </summary>
    private readonly struct TopSpec(int? count, double? percent, bool withTies)
    {
        public readonly int? Count = count;
        public readonly double? Percent = percent;
        public readonly bool WithTies = withTies;

        /// <summary>True when a PERCENT or WITH TIES cap needs the buffered path.</summary>
        public bool RequiresBuffering => this.Percent is not null || this.WithTies;
    }

    /// <summary>
    /// Computes the effective row cap for a buffered, ORDER-BY-sorted result,
    /// honoring <c>TOP n</c>, <c>TOP n PERCENT</c> (ceil of the total count),
    /// and <c>WITH TIES</c> (extends the cap while the ORDER BY keys equal the
    /// boundary row's). Returns <c>null</c> for "no cap" — when neither TOP nor
    /// <paramref name="fetchCount"/> applies.
    /// </summary>
    private static int? ComputeTopCap<T>(List<T> rows, Func<T, SqlValue[]> keysOf, List<OrderBySpec> orderBy, TopSpec top, int? fetchCount)
    {
        var cap = top.Percent is { } pct
            ? (int)Math.Ceiling(rows.Count * pct / 100.0)
            : top.Count ?? fetchCount;
        if (cap is not { } c || !top.WithTies || orderBy.Count == 0 || c <= 0 || c >= rows.Count)
            return cap;
        var boundary = keysOf(rows[c - 1]);
        while (c < rows.Count && CompareOrderKeys(keysOf(rows[c]), boundary, orderBy) == 0)
            c++;
        return c;
    }

    /// <summary>
    /// A parsed <c>TOP (expr) [PERCENT]</c> limit on an UPDATE / DELETE /
    /// INSERT statement. Unlike SELECT's <c>TOP</c>, the DML grammar requires
    /// the parentheses — the legacy bare form (<c>UPDATE TOP 2 …</c>) is a
    /// syntax error (Msg 102) on real SQL Server.
    /// </summary>
    internal readonly struct DmlTopLimit(Expression expression, bool percent)
    {
        public readonly Expression Expression = expression;
        public readonly bool Percent = percent;
    }

    /// <summary>
    /// Parses a leading <c>TOP (expr) [PERCENT]</c> on a DML statement when
    /// present. Called with the cursor on the token immediately after the DML
    /// verb (or after INSERT's optional <c>INTO</c>). Returns <c>null</c> when
    /// the current token isn't <c>TOP</c>, leaving the cursor untouched;
    /// otherwise consumes the whole clause and leaves the cursor on the token
    /// that follows it. The parentheses are mandatory — a bare <c>TOP 2</c>
    /// raises Msg 102 (the legacy no-paren form is SELECT-only).
    /// </summary>
    internal static DmlTopLimit? ParseDmlTopClause(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Top })
            return null;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // Passing the cursor at '(' lets Expression.Parse consume the whole
        // parenthesized expression (numeric, arithmetic, @variable, or a
        // parenthesized scalar subquery) and land on the following token.
        var expression = Expression.Parse(context);
        var percent = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Percent })
        {
            percent = true;
            context.MoveNextRequired();
        }
        return new DmlTopLimit(expression, percent);
    }

    /// <summary>
    /// Resolves a DML <c>TOP</c> limit to a concrete row cap given the number
    /// of candidate rows already collected. Validates the value the way SQL
    /// Server does: a non-PERCENT value must be a non-negative integer
    /// (Msg 1060 for non-integer / NULL, Msg 127 for negative); a PERCENT
    /// value must be numeric in [0, 100] (Msg 1031, Msg 1014 for NULL), and
    /// the cap is <c>ceil(candidateCount * pct / 100)</c> — probe-confirmed
    /// against SQL Server 2025.
    /// </summary>
    internal static int ResolveDmlTopCap(DmlTopLimit limit, int candidateCount, BatchContext batch)
    {
        var resolved = limit.Expression.Run(new RuntimeContext(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), batch));
        if (limit.Percent)
        {
            var pct = resolved.IsNull
                ? throw SimulatedSqlException.TopClauseInvalidValue()
                : resolved.CoerceTo(SqlType.Float).AsDouble;
            return pct is < 0 or > 100
                ? throw SimulatedSqlException.TopPercentOutOfRange()
                : (int)Math.Ceiling(candidateCount * pct / 100.0);
        }
        var count = resolved.IsNull || !(SqlType.IsIntegerCategory(resolved.Type) || resolved.Type is DecimalSqlType { scale: 0 })
            ? throw SimulatedSqlException.TopFetchRequiresInteger()
            : resolved.CoerceTo(SqlType.BigInt).AsInt64;
        return count < 0
            ? throw SimulatedSqlException.TopRowCountMustNotBeNegative()
            : count < candidateCount ? (int)count : candidateCount;
    }

    private static Selection ParseInner(ParserContext context, uint depth, List<AggregateExpression> aggregates, List<WindowExpression> windows, Func<MultiPartName, SqlType>? outerTypeResolver, bool allowOrderBy, bool projectionDiscarded)
    {
        var distinct = false;
        Expression? topExpression = null;
        var topPercent = false;
        var topWithTies = false;

        // Only the FROM-less bare-projection return below sets this back;
        // every other shape this method builds leaves it cleared.
        context.LastQuerySpecIsBareProjection = false;

        var firstToken = context.GetNextRequired();

        // DISTINCT/ALL appear before TOP. SQL Server rejects `TOP n DISTINCT`
        // at parse time (Msg 156), and the only other quantifier is ALL which
        // is the implicit default — accept it but treat as no-op. Switch (vs
        // chained ifs) lets the compiler emit a single ReservedKeyword type
        // check for both arms.
        switch (firstToken)
        {
            case ReservedKeyword { Keyword: Keyword.Distinct }:
                distinct = true;
                context.RecursiveBranchConstructs.Distinct = true;
                firstToken = context.GetNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.All }:
                firstToken = context.GetNextRequired();
                break;
        }

        if (firstToken is ReservedKeyword { Keyword: Keyword.Top })
        {
            // The TOP count is a single operand — a parenthesized expression
            // `TOP (expr)` or the legacy bare constant / variable. Parsing it as
            // a full expression would fold a following select-list star into a
            // multiplication (`TOP 1 *` → `1 * …`, `TOP (1) *` → `(1) * …`),
            // swallowing the star and failing near the next token.
            // The legacy form takes no unary prefix at all: real raises Msg 102
            // naming the operator for `TOP -1` / `TOP +1` / `TOP ~1`
            // (probe-confirmed 2026-08-03), where the parenthesized form takes
            // the sign and validates the value (Msg 127 when negative).
            // Rejecting the prefix here also keeps ParsePrimary on its
            // stops-before-any-binary-operator path — a sign would otherwise
            // absorb the following multiplicative chain, star included.
            var savedRejectInTop = context.EnterNextValueForScope(NextValueForScope.Clause);
            try
            {
                context.RecursiveBranchConstructs.TopOrOffset = true;
                if (context.MoveNextRequiredReturnSelf().Token is Operator { Character: '+' or '-' or '~' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                topExpression = Expression.ParsePrimary(context);
            }
            finally
            {
                context.NextValueForRejection = savedRejectInTop;
            }
            // `TOP n PERCENT` — cap becomes ceil(n% × rowcount). PERCENT is a
            // reserved keyword.
            if (context.Token is ReservedKeyword { Keyword: Keyword.Percent })
            {
                topPercent = true;
                context.MoveNextRequired();
            }
            // `TOP n WITH TIES` — includes rows tying the last ORDER BY value.
            // TIES is a contextual identifier (SQL Server doesn't reserve it).
            if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            {
                if (context.GetNextRequired() is not Name tiesToken
                    || !context.Batch.CurrentDatabase.Collation.Equals(tiesToken.Value, "TIES"))
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                topWithTies = true;
                context.MoveNextRequired();
            }
            // Parse-time validation of the count / percent literal, mirroring
            // SQL Server's compile-time rejection.
            if (topPercent)
                _ = ResolveTopPercentValue(topExpression, context.Batch);
            else
                _ = ResolveRowCountLimit(topExpression, RowLimitKind.Top, context.Batch);
        }

        // Both quantifiers precede the select list, so real's statement-level
        // NEXT VALUE FOR refusals for them are in force before anything can
        // draw from a sequence: Msg 11721 for DISTINCT, Msg 11739 for TOP.
        // The floor holds for this branch's whole parse and is lifted by
        // ParseSingleSelectStatement, which owns the branch's context frame.
        // (An OFFSET earns Msg 11739 too, but it can only follow an ORDER BY,
        // whose Msg 11723 outranks it — so it needs no separate gate.)
        // A session `SET ROWCOUNT` earns the same Msg 11739 as a written TOP —
        // real's message names all three sources ("if ROWCOUNT option has been
        // set, or the query contains TOP or OFFSET") and refuses on the
        // option alone (probe-confirmed).
        if (distinct)
            _ = context.EnterNextValueForScope(NextValueForScope.Deduplicating);
        else if (topExpression is not null || context.Connection.RowCountLimit > 0)
            _ = context.EnterNextValueForScope(NextValueForScope.RowLimited);

        // Msg 11723 is a property of the finished statement rather than of the
        // reference's own position, so it is settled below against this
        // snapshot once the ORDER BY has been read.
        var sequenceDrawsBefore = context.SequenceDrawsParsed;
        var unwindowedSequenceDrawsBefore = context.UnwindowedSequenceDrawsParsed;

        List<Expression> expressions = [];
        var fromClause = new FromClause();
        MultiPartName? intoTarget = null;

        // Bind the FROM clause before the select list, the way SQL Server's
        // binder does. The select list is written first but *resolves* against
        // the FROM sources, and a subquery in it can reference them
        // (`SELECT (SELECT t.col) FROM t`) — which needs the scope in place at
        // parse time, because a projection's type is resolved statically by
        // GetSqlType rather than deferred to Run the way a WHERE reference is.
        // Sources are parsed exactly once: the cursor jumps to the FROM
        // keyword, parses them, then rewinds to the select list, and the
        // loop's own FROM arm resumes from `afterSources` instead of
        // re-parsing. A FROM-less SELECT skips all of this.
        List<FromSource>? preParsedSources = null;
        List<JoinSpec>? preParsedJoins = null;
        ParserContext.Checkpoint afterSources = default;
        if (FindOwnFromClause(context) is { } fromCheckpoint)
        {
            var selectListStart = context.SaveCheckpoint();
            context.RestoreCheckpoint(fromCheckpoint);
            var candidateSources = new List<FromSource>();
            var candidateJoins = new List<JoinSpec>();
            try
            {
                ParseSourcesAndJoins(context, depth, candidateSources, candidateJoins, outerTypeResolver);
                afterSources = context.SaveCheckpoint();
                preParsedSources = candidateSources;
                preParsedJoins = candidateJoins;
            }
            catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
            {
                // The pre-pass is speculative: it only exists to have the
                // scope ready while the select list parses. If the FROM can't
                // be parsed on its own — an unresolvable table, a skip-mode
                // dead branch, a statement-level error the normal order would
                // have reported first (a CTE's Msg 319 outranking a Msg 208) —
                // discard it and leave the original path to parse the FROM in
                // place, so error identity and ordering stay exactly as they
                // were. Nothing is kept from the failed attempt.
                preParsedSources = null;
                preParsedJoins = null;
            }

            context.RestoreCheckpoint(selectListStart);

            // Chain this scope ahead of any enclosing one, so a select-list
            // subquery resolves outer columns at every nesting level.
            if (preParsedSources is { } scope)
            {
                var scopeSources = scope.ToArray();
                context.OuterTypeResolver = name => ResolveColumnTypeAcrossSources(scopeSources, name, outerTypeResolver);
                // A projection-level CONTAINS / FREETEXT (`CASE WHEN
                // CONTAINS(col, 'x') THEN …`) and a spatial column's property
                // form (`Location.Lat`) both bind against the same scope.
                context.ScopeSources = scopeSources;
            }
        }

        // No FROM of this statement's own bound (there is none, or the
        // speculative pre-pass above discarded it): install the enclosing
        // scope directly. A nested subquery reads its outer chain from
        // `context.OuterTypeResolver` — it never sees this parse's
        // `outerTypeResolver` argument — so leaving whatever the enclosing
        // parse happened to have there is what made a FROM-less APPLY body's
        // `WHERE EXISTS (… c.k …)` fail to bind against the APPLY's left side
        // (a body carrying its own FROM installed the chain and worked).
        if (preParsedSources is null && outerTypeResolver is not null)
            context.OuterTypeResolver = outerTypeResolver;

        // A FROM-less SELECT bakes its projection at parse time, which is only
        // sound while nothing in it can read an enclosing row. A subquery can:
        // `VisitColumnReferences` doesn't descend into a nested query body, so
        // the reference test below it misses `SELECT (SELECT o.k FROM …)` and
        // the bake evaluated the inner plan against a resolver that refuses
        // every name. Count the subqueries this statement parses instead — the
        // same snapshot-and-compare the aggregate binder uses — and defer the
        // projection to the executor whenever it saw one.
        var subqueriesBeforeProjection = context.SubqueriesParsed;

        // Whether the next token should begin a select-list element: true at
        // the start and after a comma, false once an element (and any alias it
        // took) is complete.
        var elementExpected = true;
        do
        {
            // A keyword standing where an element belongs means the list never
            // began (or a comma promised one that never arrived): real reports
            // Msg 156 naming that keyword, where the statement-boundary arms
            // below would otherwise end the projection and leave a short or
            // zero-column SELECT behind. End-of-input isn't a keyword, so a
            // bare SELECT keeps its Msg 102.
            if (elementExpected && context.Token is ReservedKeyword blocking && !CanBeginProjectionElement(blocking))
                throw SimulatedSqlException.SyntaxErrorNearKeyword(blocking);

            // The mirror case: this switch is re-entered after an alias was
            // taken, so a further value token is one too many for a single
            // element — `SELECT 1 xyz 2` is Msg 102 at the `2`, not a second
            // column. Only a comma or a clause keyword may follow a complete,
            // aliased element (probe-confirmed).
            if (!elementExpected && StartsProjectionElement(context.Token))
                throw SimulatedSqlException.SyntaxErrorNear(context);

            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.From }:
                case ReservedKeyword { Keyword: Keyword.Into }:
                // A FROM-less SELECT can still carry a trailing ORDER BY
                // (legal on real SQL Server: `SELECT 2 AS X ORDER BY X`
                // returns the one row) or WHERE (`SELECT 1 AS x WHERE 1 = 1`;
                // SMO's PolicyStore enumeration uses the aliased FROM-less
                // WHERE shape). When the final projection element ended in an
                // alias, the alias-continue routes ORDER / WHERE back to this
                // pre-expression switch; fall through (like FROM / INTO) to the
                // post-expression handlers, which consume the clause and its
                // OFFSET / FETCH tail. Sorting is a no-op on the one
                // synthesized row, but the clause must parse rather than raise.
                case ReservedKeyword { Keyword: Keyword.Order }:
                case ReservedKeyword { Keyword: Keyword.Where }:
                    break;

                // A trailing FOR (JSON / XML / BROWSE) after an aliased final
                // projection element on a FROM-less SELECT reaches this pre-
                // expression switch via the alias-continue; end the projection
                // so ParseQueryExpression can handle FOR JSON (or leave any
                // other FOR clause for the downstream Msg 102).
                case ReservedKeyword { Keyword: Keyword.For }:
                    goto ExitWhileTokenLoop;

                case ReservedKeyword { Keyword: Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert or Keyword.Coalesce or Keyword.NullIf or Keyword.Case or Keyword.Current_Timestamp or Keyword.Current_Date or Keyword.Current_User or Keyword.Session_User or Keyword.System_user or Keyword.User }:
                    // LEFT, RIGHT, CONVERT, TRY_CONVERT, COALESCE, NULLIF are
                    // reserved keywords but valid as function-call heads
                    // inside a SELECT projection. CASE introduces an inline
                    // expression (see CaseExpression.ParseCase). CURRENT_TIMESTAMP
                    // is uniquely a parens-less reserved-keyword expression
                    // (see CurrentTimeFunction). GROUPING / GROUPING_ID are
                    // contextual keywords (not reserved) so they reach
                    // Expression.Parse via the default UnquotedString path.
                    expressions.Add(Expression.Parse(context));
                    break;

                // Set-op keywords at the outer-switch position (i.e. after
                // an `AS alias` continued the loop) terminate this branch
                // so the set-op driver can chain.
                case ReservedKeyword { Keyword: Keyword.Union or Keyword.Intersect or Keyword.Except or Keyword.Option }:
                    goto ExitWhileTokenLoop;

                // WITH at the start of a projection element is unambiguous:
                // it can only mean a CTE-prefixed follow-up statement. Real
                // SQL Server raises Msg 319 here rather than the generic
                // Msg 156 from the catch-all below — telling the user to
                // separate statements with `;`. Checked before the general
                // statement-boundary case (which also treats WITH as a
                // boundary) so the more specific Msg 319 wins.
                case ReservedKeyword { Keyword: Keyword.With } when depth == 0:
                    throw SimulatedSqlException.CteRequiresPrecedingSemicolon();

                // At the top level (depth 0), the start of another statement
                // terminates this SELECT and lets the dispatch loop pick up
                // where it left off. Real SQL Server allows back-to-back
                // statements without `;` between them; we mirror by stopping
                // the projection-list parse here. Inside a subquery (depth > 0)
                // these keywords are still invalid — fall through to the
                // generic Msg 156 catch-all below.
                case Operator { Character: ';' } when depth == 0:
                    goto ExitWhileTokenLoop;
                case ReservedKeyword statementStart when depth == 0 && Simulation.IsStatementBoundary(statementStart):
                    goto ExitWhileTokenLoop;

                case ReservedKeyword { Keyword: not Keyword.Null } keyword:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);

                case Operator { Character: ',' }:
                    if (expressions.Count == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    // Reached when a comma follows an aliased element, whose
                    // alias arm re-entered this switch. Still expecting one.
                    elementExpected = true;
                    continue;
                case Operator { Character: ')' }:
                    if (depth == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    goto ExitWhileTokenLoop;

                // Bare `*` as a projection element (the first thing after
                // SELECT, or the first thing after a comma). Within a
                // projected expression, `*` is the multiplication operator and
                // is handled by Expression.Parse's binary loop instead.
                case Operator { Character: '*' }:
                    expressions.Add(new StarProjection(null));
                    context.MoveNextOptional();
                    break;

                // SELECT-assign disambiguation: `@v = expr` at projection-
                // element-start position is variable assignment;
                // `@v` followed by anything else (`+`, `,`, AS, etc.) is just
                // a variable read. Peek past the @v token to decide.
                case AtPrefixedString atPrefixed:
                    {
                        var checkpoint = context.SaveCheckpoint();
                        _ = context.MoveNext();
                        if (context.Token is Operator { Character: '=' })
                        {
                            var slot = context.Batch.GetVariableSlot(atPrefixed.Value);
                            context.MoveNextRequired();
                            var rhs = Expression.Parse(context);
                            expressions.Add(new AssignmentExpression(slot, rhs));
                        }
                        else
                        {
                            context.RestoreCheckpoint(checkpoint);
                            expressions.Add(Expression.Parse(context));
                        }
                    }
                    break;

                // Column-alias-on-left shorthand: `alias = expr` at
                // projection-element-start position is equivalent to
                // `expr AS alias`. Peek past the Name token to disambiguate
                // from a column-reference-in-comparison expression.
                case Name aliasCandidate:
                    {
                        var checkpoint = context.SaveCheckpoint();
                        _ = context.MoveNext();
                        if (context.Token is Operator { Character: '=' })
                        {
                            context.MoveNextRequired();
                            var rhs = Expression.Parse(context);
                            expressions.Add(AssignColumnAlias(rhs, aliasCandidate.Value));
                        }
                        else
                        {
                            context.RestoreCheckpoint(checkpoint);
                            expressions.Add(Expression.Parse(context));
                        }
                    }
                    break;

                // Column-alias-on-left with a string-literal name: the legacy
                // `'alias' = expr` form. A string literal at projection-
                // element-start is otherwise a projected value, so peek past
                // it for `=` exactly as the identifier form above does. Only
                // string literals qualify — binary (`0x…`) literals fall to
                // the default value-parse path.
                case Literal { Value.Type.Category: SqlTypeCategory.String } aliasLiteralCandidate:
                    {
                        var checkpoint = context.SaveCheckpoint();
                        _ = context.MoveNext();
                        if (context.Token is Operator { Character: '=' })
                        {
                            context.MoveNextRequired();
                            var rhs = Expression.Parse(context);
                            expressions.Add(AssignColumnAlias(rhs, aliasLiteralCandidate.Value.AsString));
                        }
                        else
                        {
                            context.RestoreCheckpoint(checkpoint);
                            expressions.Add(Expression.Parse(context));
                        }
                    }
                    break;

                default:
                    expressions.Add(Expression.Parse(context));
                    break;
            }

            // An element was produced above. Only the comma arms below put the
            // loop back into element-expected state; the alias arms leave it
            // here, which is what makes a second value token an error.
            elementExpected = false;

            switch (context.Token)
            {
                case null:
                    goto ExitWhileTokenLoop;

                // End of statement in a multi-statement batch. Leave the ';'
                // as the current token so the outer dispatch loop sees it on
                // its next iteration (where it's a no-op separator) and
                // continues with whatever statement follows.
                case Operator { Character: ';' }:
                    goto ExitWhileTokenLoop;

                case Operator { Character: ',' }:
                    elementExpected = true;
                    continue;

                // A `)` at the lookahead-after-expression position closes the
                // enclosing subquery / derived table when this Parse is at
                // depth > 0. The pre-expression switch above also has a `)`
                // case for the empty-projection error path; this one fires
                // when at least one expression has been parsed.
                case Operator { Character: ')' }:
                    if (depth == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    goto ExitWhileTokenLoop;

                case Name name:
                    expressions[^1] = AssignColumnAlias(expressions[^1], name.Value);
                    continue;

                // Bare postfix string-literal alias: `expr 'alias'`. T-SQL has
                // no implicit string concatenation, so a string literal
                // directly following a complete select-list expression is
                // always an alias (including when the expression is itself a
                // string literal). Binary literals aren't valid aliases and
                // fall through to the Msg 102 catch-all below.
                case Literal { Value.Type.Category: SqlTypeCategory.String } aliasLiteral:
                    expressions[^1] = AssignColumnAlias(expressions[^1], aliasLiteral.Value.AsString);
                    continue;

                case ReservedKeyword { Keyword: Keyword.As }:
                    expressions[^1] = AssignColumnAlias(expressions[^1], ReadAliasName(context.GetNextRequired()));
                    continue;

                case ReservedKeyword { Keyword: Keyword.From }:
                    List<FromSource> sources;
                    List<JoinSpec> joins;
                    if (preParsedSources is not null)
                    {
                        // Sources came from the pre-pass; resume from the token
                        // after them and consume only the WHERE tail.
                        sources = preParsedSources;
                        joins = preParsedJoins!;
                        context.RestoreCheckpoint(afterSources);
                        ConsumeWhereOrderByWithOuterScope(context, fromClause, [.. sources], outerTypeResolver, allowOrderBy, depth);
                    }
                    else
                    {
                        sources = [];
                        joins = [];
                        ParseFromSourceAndJoins(context, depth, sources, joins, fromClause, outerTypeResolver, allowOrderBy);
                    }

                    if (topExpression is not null && fromClause.OffsetExpression is not null)
                        throw SimulatedSqlException.TopAndOffsetMutuallyExclusive();
                    RejectSequenceDrawUnderOrderBy(context, fromClause, sequenceDrawsBefore, unwindowedSequenceDrawsBefore);
                    ExpandStars(context.Batch.CurrentDatabase.Collation, expressions, sources);
                    JoinSpec[] joinArray = [.. joins];
                    var plan = BuildSqlProjection(context.Batch, [.. sources], joinArray, expressions, fromClause, distinct, topExpression, topPercent, topWithTies, aggregates, windows, outerTypeResolver, ResolveAssignmentMode(expressions), intoTarget, context.ReadColumnSink, projectionDiscarded);
                    // The decorrelated key plan an enclosing EXISTS / IN can
                    // answer itself from; null for every body that isn't
                    // equi-correlated, which is every top-level query.
                    plan.SemiJoin = TryBuildSemiJoinShape(
                        context.Batch, plan, [.. sources], joinArray, expressions, fromClause,
                        distinct, topExpression, aggregates, windows, outerTypeResolver);
                    return plan;

                // SELECT projection INTO target [FROM ...] — captures the
                // destination table name. Real SQL Server requires every
                // projection to have a name (Msg 1038) and rejects duplicate
                // names (Msg 2705); both validations happen at build time
                // alongside the schema-inference walk, so we can flag the
                // offending column with the target table name in the message.
                case ReservedKeyword { Keyword: Keyword.Into }:
                    if (intoTarget is not null)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    intoTarget = BatchContext.ParseObjectName(context);
                    continue;

                // WHERE / GROUP BY / HAVING with no FROM clause. All three are
                // legal against the one synthesized row a source-less SELECT
                // reads (`SELECT COUNT(*) HAVING COUNT(*) > 0` → 1 row,
                // `SELECT 1 GROUP BY ()` → 1 row, probe-confirmed), and
                // ConsumeWhereAndOrderBy already reads them in grammar order
                // from whichever of the three the cursor sits on.
                case ReservedKeyword { Keyword: Keyword.Where or Keyword.Group or Keyword.Having }:
                    ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy, depth);
                    goto ExitWhileTokenLoop;

                case ReservedKeyword { Keyword: Keyword.Order }:
                    if (allowOrderBy || depth == context.ParenthesizedInsertSourceDepth)
                        ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy, depth);
                    // When this branch is part of a set-op chain, leave
                    // the cursor on ORDER for the top-level driver to
                    // consume (or for the outer caller to error on, per
                    // SQL Server's per-branch-ORDER-BY rejection).
                    goto ExitWhileTokenLoop;

                // Set-op keywords terminate a branch parse so the outer
                // driver (ParseQueryExpression) can chain branches.
                case ReservedKeyword { Keyword: Keyword.Union or Keyword.Intersect or Keyword.Except or Keyword.Option }:
                    goto ExitWhileTokenLoop;

                // A trailing FOR (JSON / XML / BROWSE) on a FROM-less SELECT
                // ends the projection; ParseQueryExpression handles FOR JSON and
                // leaves any other FOR clause for the downstream Msg 102.
                case ReservedKeyword { Keyword: Keyword.For }:
                    goto ExitWhileTokenLoop;

                // WITH at the projection-element-end position can only mean a
                // CTE-prefixed follow-up statement; raise Msg 319 to mirror
                // SQL Server's specific error here. Checked before the general
                // statement-boundary case (which also treats WITH as a
                // boundary) so the more specific Msg 319 wins.
                case ReservedKeyword { Keyword: Keyword.With } when depth == 0:
                    throw SimulatedSqlException.CteRequiresPrecedingSemicolon();

                // At the top level (depth 0), the start of another statement
                // terminates this SELECT — the dispatch loop picks up there.
                // Inside a subquery these keywords stay invalid (fall through
                // to the generic Msg 102 below).
                case ReservedKeyword statementStart when depth == 0 && Simulation.IsStatementBoundary(statementStart):
                    goto ExitWhileTokenLoop;

                // A boolean-predicate keyword directly after a complete
                // select-list value means the user wrote a predicate where a
                // projected value was expected (`SELECT 'a' LIKE '…'`). Real
                // SQL Server reports Msg 156 near the keyword, not the generic
                // Msg 102 (probe-confirmed 2026-07-21 for LIKE / IN / IS /
                // BETWEEN against SQL Server 2025).
                case ReservedKeyword { Keyword: Keyword.Like or Keyword.In or Keyword.Is or Keyword.Between } predicateKeyword:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(predicateKeyword);
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.GetNextOptional() is not null);
    ExitWhileTokenLoop:

        // A comma that promised an element the input never supplied — real
        // reports Msg 102 at the comma itself. Reached when end-of-statement
        // followed the comma directly; a comma followed by a *keyword* raised
        // Msg 156 at the top of the loop instead.
        if (elementExpected && expressions.Count > 0)
            throw SimulatedSqlException.SyntaxErrorNear(',');

        if (topExpression is not null && fromClause.OffsetExpression is not null)
            throw SimulatedSqlException.TopAndOffsetMutuallyExclusive();
        if (topWithTies && fromClause.OrderBy.Count == 0)
            throw SimulatedSqlException.TopWithTiesRequiresOrderBy();
        RejectSequenceDrawUnderOrderBy(context, fromClause, sequenceDrawsBefore, unwindowedSequenceDrawsBefore);

        // A source-less SELECT that aggregates, groups, filters groups or
        // windows takes the ordinary projection builder over an empty source
        // array rather than the constant-row path below: real reads such a
        // query as one over a single synthesized row, so the whole aggregate /
        // GROUP BY / HAVING / window machinery applies unchanged (probe-
        // confirmed — `SELECT COUNT(*)` is 1, `SELECT COUNT(*) WHERE 1=0` is 0
        // because the implicit empty group survives a WHERE that admits no row,
        // and `SELECT COUNT(*) OVER () WHERE 1=0` is *no* rows because a window
        // has no group to collapse to). EnumerateJoinedRows supplies that one
        // row for an empty source array. Baking the projection at parse time —
        // what the constant-row path does — cannot express any of this, since
        // an aggregate's value isn't a property of the expression alone.
        if (aggregates.Count > 0 || windows.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null)
        {
            return BuildSqlProjection(context.Batch, [], [], expressions, fromClause, distinct,
                topExpression, topPercent, topWithTies, aggregates, windows,
                context.OuterTypeResolver ?? outerTypeResolver, ResolveAssignmentMode(expressions),
                intoTarget, context.ReadColumnSink, projectionDiscarded);
        }

        // A set operator one token past this branch refuses the whole statement
        // (Msg 11721) without real drawing anything, and the bake below would
        // draw while evaluating. The chain parser settles the same refusal, but
        // only after this branch has been built — so a FROM-less branch that
        // drew has to look the one token ahead itself.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect }
            && context.SequenceDrawsParsed > sequenceDrawsBefore)
        {
            throw SimulatedSqlException.NextValueForNotAllowedWithDedup();
        }

        // The FROM-less path bakes its projection values at parse time and
        // never plan-caches (BuildSynthesizedSqlRow disqualifies the batch),
        // so its counts resolve here once, exactly as its projection does.
        // The synthesized shape yields at most one row, so PERCENT collapses to
        // "1 row when pct > 0, else none".
        var containsSubquery = context.SubqueriesParsed > subqueriesBeforeProjection;
        context.LastQuerySpecIsBareProjection = !distinct
            && topExpression is null
            && intoTarget is null
            && !containsSubquery
            && fromClause.Excluders.Count == 0
            && fromClause.OrderBy.Count == 0
            && fromClause.OffsetExpression is null
            && fromClause.FetchExpression is null;

        return BuildSynthesizedSqlRow(context.Batch, expressions, fromClause.Excluders, fromClause.OrderBy,
            topPercent
                ? (topExpression is not null && ResolveTopPercentValue(topExpression, context.Batch) > 0 ? 1 : 0)
                : ResolveRowCountLimit(topExpression, RowLimitKind.Top, context.Batch),
            ResolveRowCountLimit(fromClause.OffsetExpression, RowLimitKind.Offset, context.Batch),
            ResolveRowCountLimit(fromClause.FetchExpression, RowLimitKind.Fetch, context.Batch),
            ResolveAssignmentMode(expressions), intoTarget, context.OuterTypeResolver ?? outerTypeResolver,
            containsSubquery);
    }

    /// <summary>
    /// Raises real's Msg 11723 when the finished query spec carries an
    /// <c>ORDER BY</c> and drew from a sequence somewhere the reference's own
    /// position allows. Real settles this one against the whole statement
    /// rather than the reference's position, and it outranks both the
    /// clause refusal (a <c>WHERE</c> reference in an ordered statement is
    /// 11723, not 11720) and the row-limit one — but not <c>DISTINCT</c>'s,
    /// which is why the counter only ever reaches here when nothing stricter
    /// already threw. A reference carrying its own <c>OVER</c> never counts:
    /// that is the one exemption real's message names, and the one exemption
    /// it grants anywhere (probe-confirmed 2026-08-05).
    /// </summary>
    private static void RejectSequenceDrawUnderOrderBy(ParserContext context, FromClause fromClause, int sequenceDrawsBefore, int unwindowedSequenceDrawsBefore)
    {
        if (fromClause.OrderBy.Count > 0 && context.UnwindowedSequenceDrawsParsed > unwindowedSequenceDrawsBefore)
            throw SimulatedSqlException.NextValueForNotAllowedWithOrderBy();
        // An OFFSET can only follow an ORDER BY, so this is reached only for a
        // draw the OVER exemption carried past the check above — real refuses
        // that one too, since the OVER lifts the ORDER BY refusal alone.
        if (fromClause.OffsetExpression is not null && context.SequenceDrawsParsed > sequenceDrawsBefore)
            throw SimulatedSqlException.NextValueForNotAllowedWithRowLimit();
    }

    /// <summary>
    /// Wraps a projection expression in its column alias, mirroring SQL
    /// Server's rejection of an empty alias ("" / [] / '' / N'') with Msg
    /// 1038. Shared by every select-list alias site: the AS form, the bare
    /// postfix form, and the alias-on-left <c>alias = expr</c> form.
    /// </summary>
    private static NamedExpression AssignColumnAlias(Expression expression, string alias) =>
        alias.Length == 0
            ? throw SimulatedSqlException.EmptyColumnAlias()
            : new NamedExpression(expression, alias);

    /// <summary>
    /// Reads a column-alias name from the token following <c>AS</c>: an
    /// identifier (quoted, bracketed, or bare) or a string literal
    /// (single-quoted or <c>N</c>-prefixed). Anything else is Msg 102.
    /// </summary>
    private static string ReadAliasName(Token token) => token switch
    {
        Name name => name.Value,
        Literal { Value.Type.Category: SqlTypeCategory.String } literal => literal.Value.AsString,
        // A reserved keyword can't stand in as an alias, and real names it:
        // `SELECT 1 AS user` → Msg 156, not the generic Msg 102. The
        // compatibility-gated `REGEXP_LIKE` reaches here the same way at
        // level 170.
        ReservedKeyword reserved => throw SimulatedSqlException.SyntaxErrorNearKeyword(reserved),
        _ => throw SimulatedSqlException.SyntaxErrorNear(token),
    };

    /// <summary>
    /// Walks the parsed projection list to detect <c>SELECT @v = expr</c>
    /// mode. Returns true when every projection element is an
    /// <see cref="AssignmentExpression"/>; false when none are; raises Msg
    /// 141 when the projection mixes assignment and retrieval elements
    /// (probe-confirmed real SQL Server behavior).
    /// </summary>
    private static bool ResolveAssignmentMode(List<Expression> expressions)
    {
        if (expressions.Count == 0) return false;
        var assignCount = 0;
        for (var i = 0; i < expressions.Count; i++)
        {
            if (expressions[i] is AssignmentExpression)
                assignCount++;
        }
        return assignCount switch
        {
            0 => false,
            var n when n == expressions.Count => true,
            _ => throw SimulatedSqlException.SelectAssignmentMixedWithRetrieval(),
        };
    }

    /// <summary>
    /// Parses the FROM clause: the leftmost source plus zero or more JOIN
    /// clauses, followed by the optional WHERE / GROUP BY / HAVING /
    /// ORDER BY tail. Builds the <see cref="FromSource"/>[] /
    /// <see cref="JoinSpec"/>[] pair the projector consumes, and registers
    /// the multi-source type resolver in
    /// <see cref="ParserContext.OuterTypeResolver"/> so any subqueries
    /// inside WHERE / HAVING / ON predicates see the chained scope stack.
    /// </summary>
    /// <remarks>
    /// On entry, <see cref="ParserContext.Token"/> is the FROM keyword.
    /// On return, the cursor is positioned past the WHERE / GROUP BY /
    /// HAVING / ORDER BY tail, ready for the outer dispatch loop to
    /// observe the next un-consumed token.
    /// </remarks>
    /// <summary>
    /// Scans forward from the cursor for this SELECT's own <c>FROM</c> keyword
    /// and returns a checkpoint positioned on it, or <see langword="null"/>
    /// when the statement has no FROM (<c>SELECT 1</c>) or the scan leaves the
    /// statement first. The cursor is left where it started — this only looks.
    /// </summary>
    /// <remarks>
    /// Paren depth keeps nested constructs out of the match: a scalar subquery
    /// or derived table in the select list carries its own FROM, and only a
    /// depth-0 keyword belongs to this SELECT. A depth-0 set-operation keyword
    /// ends the search, because a FROM after it belongs to the next branch;
    /// a closing paren that drops depth below zero means the enclosing
    /// subquery ended first.
    /// </remarks>
    private static ParserContext.Checkpoint? FindOwnFromClause(ParserContext context)
    {
        var start = context.SaveCheckpoint();
        try
        {
            var parenDepth = 0;
            var previousWasDistinct = false;
            while (true)
            {
                // `IS [NOT] DISTINCT FROM` puts a FROM keyword at depth 0 that
                // is part of an expression, not a clause. It always follows
                // DISTINCT directly, whereas `SELECT DISTINCT … FROM` has the
                // select list in between, so one token of history separates
                // them. Every other FROM-bearing construct (TRIM / EXTRACT /
                // SUBSTRING's ANSI forms) is parenthesized and so is already
                // excluded by depth.
                var tokenIsDistinct = context.Token is ReservedKeyword { Keyword: Keyword.Distinct };
                switch (context.Token)
                {
                    case null:
                        return null;
                    case Operator { Character: '(' }:
                        parenDepth++;
                        break;
                    case Operator { Character: ')' }:
                        if (--parenDepth < 0)
                            return null;
                        break;
                    case Operator { Character: ';' }:
                        return null;
                    case ReservedKeyword { Keyword: Keyword.From } when parenDepth == 0 && !previousWasDistinct:
                        return context.SaveCheckpoint();
                    case ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect } when parenDepth == 0:
                        return null;
                }

                previousWasDistinct = tokenIsDistinct;
                if (!context.MoveNext())
                    return null;
            }
        }
        finally
        {
            context.RestoreCheckpoint(start);
        }
    }

    private static void ParseFromSourceAndJoins(
        ParserContext context,
        uint depth,
        List<FromSource> sources,
        List<JoinSpec> joins,
        FromClause fromClause,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        bool allowOrderBy)
    {
        ParseSourcesAndJoins(context, depth, sources, joins, outerTypeResolver);

        // Now register the multi-source type resolver and parse WHERE / etc.
        ConsumeWhereOrderByWithOuterScope(context, fromClause, [.. sources], outerTypeResolver, allowOrderBy, depth);
    }

    /// <summary>
    /// Pure source-and-joins parser, separable from WHERE / ORDER BY
    /// consumption. Used by both <see cref="ParseFromSourceAndJoins"/> (which
    /// adds WHERE consumption on top) and the UPDATE / DELETE mutation paths
    /// (which handle WHERE separately because the leading-identifier target
    /// binding has to happen first). Enters with the cursor on the
    /// <c>FROM</c> keyword (or, in mutation context, on the FROM keyword
    /// position); leaves the cursor at the lookahead-after-last-source token
    /// (typically WHERE, end-of-statement, or set-op chain).
    /// </summary>
    internal static void ParseSourcesAndJoins(
        ParserContext context,
        uint depth,
        List<FromSource> sources,
        List<JoinSpec> joins,
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        // A FROM clause at any nesting depth is what makes a function body's
        // rejected SELECT real's Msg 444 state 2 rather than state 3.
        FunctionBodyShape.NoteRowsetRead(context);
        // Every column reference a non-APPLY source's own arguments name, kept
        // until the whole FROM is parsed — a source may name a sibling written
        // after it, so the check can't run per source. Local to this FROM, so a
        // nested one validates against its own sources.
        var siblingCandidates = new List<Reference>();
        ParseExplicitJoinChain(context, depth, sources, joins, outerTypeResolver, siblingCandidates);

        // Comma-separated FROM (ANSI-89 syntax) binds at lower precedence than
        // explicit JOINs: `FROM a, b JOIN c ON p` means `a CROSS JOIN (b JOIN c
        // ON p)`. Each comma starts a fresh explicit-join chain; a Cross
        // JoinSpec splices the chains together so the runtime JoinDriver folds
        // them into a Cartesian product (filtered later by WHERE). The comma
        // itself isn't consumed here — ParseSingleFromSource starts with
        // GetNextRequired() to advance past whatever preceding token it was
        // handed (FROM / JOIN keyword / comma), so leave the cursor on the ',
        // for the chain's first ParseSingleFromSource call.
        while (context.Token is Operator { Character: ',' })
        {
            joins.Add(new JoinSpec(JoinKind.Cross, onPredicate: null));
            ParseExplicitJoinChain(context, depth, sources, joins, outerTypeResolver, siblingCandidates);
        }

        RejectSiblingReferences(siblingCandidates, sources, outerTypeResolver);
    }

    private static void ParseExplicitJoinChain(
        ParserContext context,
        uint depth,
        List<FromSource> sources,
        List<JoinSpec> joins,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        List<Reference> siblingCandidates)
    {
        // A parenthesized join group as the leftmost item — `(A JOIN B ON …)
        // [LEFT] JOIN C …` — is a pure grammar grouping: a left-deep spine
        // already groups its left operand, so the group's interior sources /
        // joins splice directly into this chain with no group marker.
        if (NextSourceIsJoinGroup(context))
            ParseJoinGroup(context, depth, sources, joins, outerTypeResolver, siblingCandidates);
        else
            sources.Add(ParseSourceCollectingColumnReads(context, depth, outerTypeResolver, sources, siblingCandidates));

        // Parse JOIN clauses. ParseSingleFromSource ends with the cursor at
        // the lookahead-after-source token (e.g. WHERE, ORDER, JOIN, INNER,
        // LEFT, CROSS, etc.). Loop while we see a JOIN-introducing keyword.
        while (TryParseJoinKeyword(context, out var kind))
        {
            if (kind is JoinKind.CrossApply or JoinKind.OuterApply)
            {
                sources.Add(ParseLateralFromSource(context, depth, sources, outerTypeResolver));
                if (context.Token is ReservedKeyword { Keyword: Keyword.On } onToken)
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(onToken);
                joins.Add(new JoinSpec(kind, onPredicate: null));
                continue;
            }

            // A parenthesized join group as this join's right operand —
            // `A LEFT JOIN (B JOIN C ON c1) ON c2` — changes associativity from
            // the default left-deep fold: the interior join binds first, then
            // this ON joins the accumulated left spine against the whole group
            // (an outer-join miss NULL-fills every group slot). The interior
            // sources / joins are spliced by ParseJoinGroup; the connecting
            // JoinSpec (carrying GroupCount) is inserted at the group's leading
            // slot, ahead of the interior joins ParseJoinGroup appended.
            if (NextSourceIsJoinGroup(context))
            {
                var groupStart = sources.Count;
                // The connecting join is inserted ahead of the interior joins
                // ParseJoinGroup appends. Capture the insertion index now: the
                // flat `joins.Count == sources.Count - 1` invariant doesn't hold
                // mid-parse of an enclosing group (its own connecting join is
                // inserted only after this nested group finishes), so
                // `groupStart - 1` would misplace the join under nesting.
                var groupJoinIndex = joins.Count;
                ParseJoinGroup(context, depth, sources, joins, outerTypeResolver, siblingCandidates);
                var groupCount = sources.Count - groupStart;
                BooleanExpression? groupOn = null;
                if (kind == JoinKind.Cross)
                {
                    if (context.Token is ReservedKeyword { Keyword: Keyword.On })
                        throw SimulatedSqlException.SyntaxErrorNearKeyword((ReservedKeyword)context.Token);
                }
                else if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
                {
                    // A group takes no alias: `(…) AS x` → Msg 156 near the AS
                    // keyword, a bare-name alias → Msg 102, matching real.
                    throw context.Token is ReservedKeyword aliasKeyword
                        ? SimulatedSqlException.SyntaxErrorNearKeyword(aliasKeyword)
                        : SimulatedSqlException.SyntaxErrorNear(context);
                }
                else
                {
                    context.MoveNextRequired();
                    groupOn = ParseOnPredicateWithScope(context, sources, outerTypeResolver);
                }
                joins.Insert(groupJoinIndex, new JoinSpec(kind, groupOn) { GroupCount = groupCount });
                continue;
            }

            // Joined-source derived tables can also correlate, but the JoinDriver
            // path for non-leftmost LateralPlan sources doesn't apply ON
            // predicates or LEFT-fill. Keep the chained outer-type-resolver in
            // play so a correlated derived table here is at least diagnosed
            // (NotSupportedException at execute time) rather than silently
            // resolving against a wrong scope.
            sources.Add(ParseSourceCollectingColumnReads(context, depth, outerTypeResolver, sources, siblingCandidates));
            BooleanExpression? on = null;
            if (kind == JoinKind.Cross)
            {
                if (context.Token is ReservedKeyword { Keyword: Keyword.On })
                    throw SimulatedSqlException.SyntaxErrorNearKeyword((ReservedKeyword)context.Token);
            }
            else
            {
                if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                // An ON predicate rejects NEXT VALUE FOR (Msg 11720), like the
                // other clauses real names in that message.
                var savedRejectInOn = context.EnterNextValueForScope(NextValueForScope.Clause);
                try
                {
                    on = ParseOnPredicateWithScope(context, sources, outerTypeResolver);
                }
                finally
                {
                    context.NextValueForRejection = savedRejectInOn;
                }
            }
            joins.Add(new JoinSpec(kind, on));
        }
    }

    /// <summary>
    /// Parses a JOIN's <c>ON</c> predicate with the sources parsed so far
    /// installed as the enclosing scope, so a subquery inside the predicate
    /// types its own projection against them — the same chaining
    /// <see cref="ConsumeWhereOrderByWithOuterScope"/> gives the WHERE clause.
    /// SMO's index-scripting query nests
    /// <c>(select min(index_id) from sys.indexes where object_id =
    /// tbl.object_id)</c> inside an ON, which needs the outer <c>tbl</c> in
    /// scope for the inner query to bind.
    /// </summary>
    private static BooleanExpression ParseOnPredicateWithScope(ParserContext context, List<FromSource> sources, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var scope = sources.ToArray();
        var saved = context.OuterTypeResolver;
        context.OuterTypeResolver = name => ResolveColumnTypeAcrossSources(scope, name, outerTypeResolver);
        try
        {
            return BooleanExpression.SimplifyForFilter(BooleanExpression.Parse(context), context);
        }
        finally
        {
            context.OuterTypeResolver = saved;
        }
    }

    /// <summary>
    /// Peeks whether the FROM source about to be parsed is a parenthesized
    /// join group — an opening <c>(</c> whose first interior token is not
    /// <c>SELECT</c> (a derived table), <c>VALUES</c> (a table-value
    /// constructor) or <c>WITH</c> (a CTE prefix, which no query in a
    /// parenthesized position may carry — routing it to the derived-table
    /// branch is what gets it real's Msg 156 instead of a join group's
    /// Msg 102). Entered with the cursor on the token preceding the source
    /// (<c>FROM</c> / a JOIN keyword / a comma / the group's own <c>(</c> when
    /// this is an interior leftmost), matching the one-token lookahead
    /// <see cref="ParseSingleFromSource"/> consumes; the checkpoint is restored
    /// so the dispatch is non-destructive.
    /// </summary>
    private static bool NextSourceIsJoinGroup(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var opensParen = context.GetNextOptional() is Operator { Character: '(' };
        var interior = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return opensParen && interior is not (null or ReservedKeyword { Keyword: Keyword.Select or Keyword.Values or Keyword.With });
    }

    /// <summary>
    /// Parses a parenthesized join group — <c>( &lt;join chain&gt; )</c> — by
    /// recursively parsing the interior chain into the same
    /// <paramref name="sources"/> / <paramref name="joins"/> lists as the
    /// enclosing FROM, so the group's members occupy their own flat slots and
    /// resolve by their own qualifiers outside the parens (a grammar grouping,
    /// not a derived-table scope). Entered with the cursor on the token
    /// preceding the opening <c>(</c>; leaves it on the lookahead token past
    /// the closing <c>)</c>. A group must contain at least one join — SQL
    /// Server rejects a parenthesized single source (<c>(t)</c>) with Msg 102.
    /// </summary>
    private static void ParseJoinGroup(
        ParserContext context,
        uint depth,
        List<FromSource> sources,
        List<JoinSpec> joins,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        List<Reference> siblingCandidates)
    {
        var joinsBefore = joins.Count;
        context.MoveNextRequired();
        ParseExplicitJoinChain(context, depth, sources, joins, outerTypeResolver, siblingCandidates);
        if (joins.Count == joinsBefore || context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
    }

    /// <summary>
    /// Parses a nested query body — a derived table or an <c>APPLY</c> right
    /// side — with <c>NEXT VALUE FOR</c> refused inside it (real's Msg 11719,
    /// see <see cref="ParserContext.NextValueForRejection"/>). The refusal is
    /// suppressed for an <c>UPDATE</c> / <c>DELETE</c>'s own <c>FROM</c>
    /// clause, which real exempts: probed 2026-08-05, an
    /// <c>UPDATE t SET … FROM (SELECT NEXT VALUE FOR s AS n) d</c> runs and
    /// draws its value where the same derived table under a <c>SELECT</c>,
    /// an <c>INSERT … SELECT</c> or a <c>MERGE … USING</c> is refused.
    /// </summary>
    private static Selection ParseNestedQueryRejectingNextValueFor(
        ParserContext context,
        uint depth,
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var saved = context.NextValueForRejection;
        if (!context.AllowNextValueForInFromClause)
            _ = context.EnterNextValueForScope(NextValueForScope.Nested);
        try
        {
            return Selection.Parse(context, depth, outerTypeResolver);
        }
        finally
        {
            context.NextValueForRejection = saved;
        }
    }

    /// <summary>
    /// Parses one <b>non-APPLY</b> FROM source with its own column references
    /// collected into <paramref name="siblingCandidates"/>. Only a source
    /// carrying <em>arguments</em> — a table-valued function, <c>STRING_SPLIT</c>
    /// / <c>OPENJSON</c> / <c>GENERATE_SERIES</c>, a <c>VALUES</c> constructor —
    /// contributes anything: a plain table names no expression, and a derived
    /// table or view body suspends the sink while its own parse runs.
    /// <see cref="ParseLateralFromSource"/> deliberately doesn't collect —
    /// <c>APPLY</c> is exactly the form that grants laterality.
    /// </summary>
    private static FromSource ParseSourceCollectingColumnReads(
        ParserContext context,
        uint depth,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        List<FromSource> sourcesSoFar,
        List<Reference> siblingCandidates)
    {
        var saved = context.FromSourceColumnSink;
        var collectedBefore = siblingCandidates.Count;
        context.FromSourceColumnSink = siblingCandidates;
        try
        {
            return ParseSingleFromSource(context, depth, outerTypeResolver);
        }
        catch (SimulatedSqlException ex) when (ex.Number == InvalidColumnNameNumber)
        {
            // A generator types its arguments as it parses them, so a sibling
            // reference that an enclosing scope can't answer raises the plain
            // "invalid column name" from inside that parse — before the
            // whole-FROM check below can see the sibling set. Re-report it as
            // the Msg 4104 real gives when the offending name is qualified by
            // a source already written to the left of this one.
            RejectSiblingReferences(siblingCandidates.GetRange(collectedBefore, siblingCandidates.Count - collectedBefore),
                sourcesSoFar, outerTypeResolver);
            throw;
        }
        finally
        {
            context.FromSourceColumnSink = saved;
        }
    }

    /// <summary>Msg 207's number, so the generator-argument catch above reads as what it matches.</summary>
    private const int InvalidColumnNameNumber = 207;

    /// <summary>
    /// SQL Server binds a non-<c>APPLY</c> FROM source's arguments in a scope
    /// that holds none of the FROM's own sources — only <c>APPLY</c> makes the
    /// right side lateral — so a reference landing on a sibling can't bind.
    /// Probed against SQL Server 2025 (2026-08-05) across <c>STRING_SPLIT</c>,
    /// <c>OPENJSON</c>, an inline and a multi-statement TVF and a <c>VALUES</c>
    /// constructor, as a <c>JOIN</c> / <c>CROSS JOIN</c> / comma / <c>LEFT
    /// JOIN</c> right side and as the leftmost source naming a later sibling:
    /// every one is <b>Msg 4104</b>, class 16 state 1, naming the written
    /// multi-part identifier — while the <em>unqualified</em> spelling is
    /// <b>Msg 207</b> on the leaf, because the name simply resolves to nothing
    /// in that scope. The same source under <c>CROSS</c> / <c>OUTER APPLY</c>,
    /// and one reading an <em>enclosing</em> query's column, both answer.
    /// <para>
    /// Real follows the 4104 with the argument's own type complaint (Msg 8116,
    /// "void type", for <c>STRING_SPLIT</c>); the simulator raises the leading
    /// error alone, as it does for every multi-error statement response.
    /// </para>
    /// </summary>
    private static void RejectSiblingReferences(
        List<Reference> siblingCandidates,
        List<FromSource> sources,
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        foreach (var candidate in siblingCandidates)
        {
            var name = candidate.ReferencedName;
            if (name.ImmediateQualifier is { } qualifier)
            {
                // A qualifier naming one of this FROM's sources is out of
                // scope here whether or not the column exists, which is why
                // `t.nosuch` is 4104 rather than 207.
                foreach (var source in sources)
                {
                    if (source.Qualifier is { } exposed && BuiltInToken.Equals(qualifier, exposed))
                        throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
                }

                continue;
            }

            // Unqualified: real reports the plain "invalid column name",
            // because the leaf resolves to nothing once the siblings are out
            // of scope. An enclosing scope that can answer it keeps it legal.
            if (!SomeSourceCarriesColumn(sources, name.Leaf) || OuterScopeResolves(outerTypeResolver, name))
                continue;

            throw SimulatedSqlException.InvalidColumnName(name);
        }
    }

    private static bool SomeSourceCarriesColumn(List<FromSource> sources, string leaf)
    {
        foreach (var source in sources)
        {
            foreach (var columnName in source.ColumnNames)
            {
                if (BuiltInToken.Equals(leaf, columnName))
                    return true;
            }
        }
        return false;
    }

    private static bool OuterScopeResolves(Func<MultiPartName, SqlType>? outerTypeResolver, MultiPartName name)
    {
        if (outerTypeResolver is null)
            return false;
        try
        {
            _ = outerTypeResolver(name);
            return true;
        }
        catch (SimulatedSqlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses the right side of <c>CROSS APPLY</c> / <c>OUTER APPLY</c>:
    /// <c>(SELECT ...) [AS alias]</c>. The inner SELECT is parsed with a
    /// chained outer-type resolver that includes <paramref name="leftSources"/>
    /// (already collected by the surrounding FROM parse) so its body's
    /// references to the left side resolve at parse time. Unlike
    /// <see cref="ParseSingleFromSource"/>, the inner is left as a deferred
    /// <see cref="Selection"/> plan on the returned <see cref="FromSource"/>;
    /// the join driver re-executes it per outer row.
    /// </summary>
    private static FromSource ParseLateralFromSource(
        ParserContext context,
        uint depth,
        List<FromSource> leftSources,
        Func<MultiPartName, SqlType>? surroundingOuter)
    {
        // Peek next token. A parenthesized derived table `(SELECT ...)`
        // stays on the dedicated path so the chained outer-type resolver
        // can be wired into the inner Selection's parse. A leading Name
        // routes to ParseSingleFromSource ONLY when it resolves to an
        // inline TVF — APPLY requires a derived table or a TVF; a bare
        // table after APPLY is invalid (probe-confirmed via real SQL
        // Server, guarded by ApplyTests).
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextRequired();

        // OPENQUERY and OPENXML are reserved keywords (not Names), so neither
        // can ride the name-string dispatch below. Neither correlates to the
        // left APPLY sources — OPENQUERY's arguments are a server identifier
        // and a constant pass-through string, OPENXML's a session document
        // handle and its patterns — so route them straight back through
        // ParseSingleFromSource.
        if (next is ReservedKeyword { Keyword: Keyword.OpenQuery or Keyword.OpenXml })
        {
            context.RestoreCheckpoint(checkpoint);
            return ParseSingleFromSource(context, depth, surroundingOuter);
        }

        if (next is Name nextName)
        {
            // The right-side source's column references can correlate to the
            // left sources of the APPLY — wire the chained resolver up front
            // so OPENJSON / STRING_SPLIT / user TVF parse-time GetSqlType
            // calls reach them.
            var leftSnapshotForName = leftSources.ToArray();
            SqlType ChainedResolverForName(MultiPartName name) =>
                ResolveColumnTypeAcrossSources(leftSnapshotForName, name, surroundingOuter);

            // Built-in rowset functions (OPENJSON, STRING_SPLIT,
            // GENERATE_SERIES) share the same APPLY-friendly shape as user-
            // defined inline TVFs — route them back through
            // ParseSingleFromSource. Case-insensitive match to mirror real
            // SQL Server's grammar.
            if (string.Equals(nextName.Value, "OPENJSON", StringComparison.OrdinalIgnoreCase)
                || string.Equals(nextName.Value, "STRING_SPLIT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(nextName.Value, "GENERATE_SERIES", StringComparison.OrdinalIgnoreCase)
                || IsRegexpRowsetName(nextName.Value, context))
            {
                context.RestoreCheckpoint(checkpoint);
                return ParseSingleFromSource(context, depth, ChainedResolverForName);
            }

            // Peek the resolved object name to decide between TVF route
            // and reject-as-syntax-error.
            var afterNameCheckpoint = context.SaveCheckpoint();
            var resolvedName = BatchContext.ParseObjectName(context);

            // xmlexpr.nodes('xquery') rowset source: the parsed object name's
            // leaf is the `nodes` method with a `(` following (ParseObjectName
            // leaves the cursor on the last segment, so peek one token past it).
            // The xml target — which may correlate to the left APPLY sources —
            // is re-parsed as an expression and the matched nodes drive a
            // lateral plan.
            if (resolvedName.Leaf.Equals("nodes", StringComparison.Ordinal))
            {
                var afterNodesLeaf = context.SaveCheckpoint();
                var followedByParen = context.MoveNext() && context.Token is Operator { Character: '(' };
                context.RestoreCheckpoint(afterNodesLeaf);
                if (followedByParen)
                {
                    context.RestoreCheckpoint(afterNameCheckpoint);
                    return ParseXmlNodesSource(context, leftSnapshotForName);
                }
            }

            var resolvedIsTvf = context.Batch.TryResolveFunction(resolvedName, out var resolvedFn)
                && resolvedFn is InlineTableValuedFunction or MultiStatementTableValuedFunction;
            // A '(' after the name marks a function-call shape (TVF invocation).
            // ParseObjectName leaves the cursor on the leaf; peek one past it.
            var isFunctionCallShape = context.MoveNext() && context.Token is Operator { Character: '(' };
            context.RestoreCheckpoint(checkpoint);
            if (resolvedIsTvf)
                return ParseSingleFromSource(context, depth, ChainedResolverForName);
            // A function-call shape that didn't resolve to a known TVF is a
            // deferred name-resolution error (Msg 208), not a syntax error:
            // real SQL Server binds the TVF name lazily, so an un-taken IF
            // branch naming an unknown function (SSMS's EngineEdition-gated
            // `CROSS APPLY sys.dm_os_volume_stats(...)` VolumeFreeSpace probe)
            // compiles and is discarded. A bare table name after APPLY stays a
            // genuine syntax error (Msg 102, probe-confirmed).
            throw isFunctionCallShape
                ? SimulatedSqlException.InvalidObjectName(resolvedName)
                : SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (next is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var afterApplyParen = context.GetNextRequired();

        var leftSnapshot = leftSources.ToArray();
        SqlType ChainedResolver(MultiPartName name) =>
            ResolveColumnTypeAcrossSources(leftSnapshot, name, surroundingOuter);

        // CROSS / OUTER APPLY (VALUES (…), (…)) alias(cols): the table value
        // constructor's rows can reference the left APPLY sources — the SSMS
        // dm_os_host_info server-properties shape. The chained resolver wires
        // that correlation in at parse and (via ForValuesConstructor) runtime.
        if (afterApplyParen is ReservedKeyword { Keyword: Keyword.Values })
            return ParseValuesDerivedTable(context, ChainedResolver);

        if (afterApplyParen is not ReservedKeyword { Keyword: Keyword.Select })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var lateralPlan = ParseNestedQueryRejectingNextValueFor(context, depth + 1, ChainedResolver);

        var schema = lateralPlan.Schema;
        var columnNames = lateralPlan.ColumnNames;
        var lateralColumns = new HeapColumn[schema.Length];
        for (var ci = 0; ci < lateralColumns.Length; ci++)
            lateralColumns[ci] = new HeapColumn(string.Empty, schema[ci], maxLength: null, nullable: true);

        var alias = ConsumeOptionalAlias(context);
        columnNames = ResolveDerivedTableColumnNames(context, columnNames, alias);

        return new FromSource(
            qualifier: alias,
            columnNames: columnNames,
            columns: lateralColumns,
            storedSchema: lateralColumns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: lateralPlan,
            lateralIsQueryBody: true);
    }

    /// <summary>
    /// Parses one FROM source (see <see cref="ParseSingleFromSourceCore"/>)
    /// and applies any trailing <c>PIVOT</c> / <c>UNPIVOT</c> table operator.
    /// The postfix wrapper lives here so both the leftmost source and every
    /// join-right source pick up PIVOT / UNPIVOT without changing their call
    /// sites; the cursor-after-source contract is preserved either way (a
    /// PIVOT / UNPIVOT clause consumes through its own alias and stops at the
    /// next lookahead token).
    /// </summary>
    /// <summary>
    /// Records a real table / view / TVF read on the active securable sink for
    /// the execution-time SELECT permission check, and hands back the
    /// <see cref="Schemas.Synonym"/> the reference was written as (null for a
    /// direct one) so the caller can stamp it on the built
    /// <see cref="FromSource"/>. A synonym is recorded as the securable in place
    /// of the object behind it — real checks the synonym and never the base.
    /// Skips temp tables (<c>#foo</c>) — those aren't permission-checked — and
    /// records nothing when no sink is active (a module body, or a context that
    /// isn't tracking reads); the synonym is resolved either way, since the
    /// joined UPDATE / DELETE paths read it off the source without a sink.
    /// </summary>
    private static Schemas.Synonym? RecordSecurableRead(ParserContext context, Schemas.SchemaObject obj, MultiPartName name)
    {
        var synonym = context.Batch.TryResolveSynonym(name, out var resolved) ? resolved : null;
        if (context.SecurableSink is { } sink && !name.Leaf.StartsWith('#'))
        {
            var securable = (Schemas.SchemaObject?)synonym ?? obj;
            sink.Add(new ReferencedSecurable(context.Batch.DatabaseFor(securable), securable.ObjectId, securable.SchemaId, securable.Name, name.ImmediateQualifier ?? Database.DefaultSchemaName));
        }
        return synonym;
    }

    /// <summary>
    /// Folds a separately-parsed plan's securable and read-column lists into the
    /// active sinks, so the reads of a body that owns its own lists — a CTE, the
    /// only such source — are checked as part of the referencing statement.
    /// A column set that is already empty, or that merges with an empty one,
    /// stays empty: that is the <c>COUNT(*)</c> shape, which requires SELECT on
    /// every column and so absorbs any narrower set.
    /// </summary>
    private static void FoldSecurables(ParserContext context, Selection plan)
    {
        if (context.SecurableSink is not { } sink || plan.ReferencedSecurables is not { } securables)
            return;
        sink.AddRange(securables);
        if (context.ReadColumnSink is not { } columnSink || plan.ReadColumnsByObject is not { } readColumns)
            return;
        foreach (var (objectId, target) in readColumns)
        {
            if (!columnSink.TryGetValue(objectId, out var existing))
            {
                columnSink[objectId] = target;
            }
            else if (existing.Ordinals.Count != 0)
            {
                if (target.Ordinals.Count == 0)
                    existing.Ordinals.Clear();
                else
                    existing.Ordinals.UnionWith(target.Ordinals);
            }
        }
    }

    private static FromSource ParseSingleFromSource(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver) =>
        ApplyOptionalPivotUnpivot(context, ParseSingleFromSourceCore(context, depth, outerTypeResolver), outerTypeResolver);

    /// <summary>
    /// Parses one FROM source: a table name (with optional alias) or a
    /// derived-table <c>(SELECT ...)</c> (with optional alias). On entry
    /// the cursor is on the FROM or JOIN keyword (caller advances past it
    /// internally via <see cref="ParserContext.GetNextRequired"/>); on
    /// return, the cursor is at the first un-consumed token after the
    /// source — typically WHERE / ORDER / a JOIN keyword / ON / etc.
    /// </summary>
    private static FromSource ParseSingleFromSourceCore(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var token = context.GetNextRequired();
        switch (token)
        {
            // A leading `.` opens a name whose db/schema positions are omitted
            // (`.[sys].[all_columns]`, `..t`) — SqlClient 7.x's SqlBulkCopy
            // metadata query reads `FROM .[sys].[all_columns]`. It routes
            // through the ordinary table path (ParseObjectName drops the empty
            // leading segments); the 1-part built-in rowset functions below
            // never carry a leading dot, so they stay gated on a Name token.
            case Name:
            case Operator { Character: '.' }:
                if (token is not Name tableName)
                    goto AfterBuiltInRowsetDispatch;

                // Built-in rowset-function dispatch wins over CTE / table
                // lookup. Case-insensitive match on the function name; each
                // parser enforces its trailing `(`. SQL Server reserves these
                // names as built-in rowset functions, so unconditional name-
                // dispatch matches real-server behavior — a CTE / table with
                // one of these names would already conflict on a real server.
                // None can carry a schema qualifier (a 2-part `dbo.OPENJSON`
                // wouldn't match a single Name token), matching real SQL
                // Server's grammar, so dispatch fires before ParseObjectName
                // / cursor advance.
                if (string.Equals(tableName.Value, "OPENJSON", StringComparison.OrdinalIgnoreCase))
                    return BuiltInRowsetSource(context, ParseOpenJson(context, outerTypeResolver));

                if (string.Equals(tableName.Value, "STRING_SPLIT", StringComparison.OrdinalIgnoreCase))
                    return BuiltInRowsetSource(context, ParseStringSplit(context, outerTypeResolver));

                // GENERATE_SERIES: single-column (`value`) plan, SQL Server 2022+.
                if (string.Equals(tableName.Value, "GENERATE_SERIES", StringComparison.OrdinalIgnoreCase))
                    return BuiltInRowsetSource(context, ParseGenerateSeries(context, outerTypeResolver));

                // The two REGEXP rowset members ship only at compatibility
                // level 170; below it the name falls through to the ordinary
                // object-name path, which raises the Msg 208 real raises.
                if (IsRegexpRowsetName(tableName.Value, context))
                {
                    return BuiltInRowsetSource(context, string.Equals(tableName.Value, "REGEXP_MATCHES", StringComparison.OrdinalIgnoreCase)
                        ? ParseRegexpMatches(context, outerTypeResolver)
                        : ParseRegexpSplitToTable(context, outerTypeResolver));
                }

                // fn_listextendedproperty: 7-arg system TVF projecting the
                // (objtype, objname, name, value) tuples for extended
                // properties matching the filter.
                if (string.Equals(tableName.Value, "fn_listextendedproperty", StringComparison.OrdinalIgnoreCase))
                    return BuiltInRowsetSource(context, ParseListExtendedProperty(context));

                // Multi-part name parse: advances the cursor past the last
                // dotted segment, leaving Token on the first non-name token
                // (alias / AS / WHERE / JOIN / etc.). CTE binding only fires
                // for a single-segment leaf (CTE names can't be schema-
                // qualified — they're aliases, not real tables).
            AfterBuiltInRowsetDispatch:
                var objectName = BatchContext.ParseObjectName(context);

                // fn_virtualfilestats: a 2-arg system TVF invoked bare or
                // `sys.`-qualified. Handled after ParseObjectName (unlike the
                // 1-part rowset functions above) precisely because it accepts
                // the `sys.` schema qualifier, so the 2-part name must be
                // parsed first. Wins over catalog-view / table lookup.
                if (BuiltInToken.Equals(objectName.Leaf, "fn_virtualfilestats")
                    && (objectName.Count == 1
                        || (objectName.Count == 2 && BuiltInToken.Equals(objectName.ImmediateQualifier, "sys"))))
                {
                    return BuiltInRowsetSource(context, ParseVirtualFileStats(context, objectName.ToString()));
                }

                // The two dependency DMVs are 2-arg system TVFs, `sys.`-qualified
                // like fn_virtualfilestats and dispatched on the same terms.
                if (objectName.Count == 2 && BuiltInToken.Equals(objectName.ImmediateQualifier, "sys"))
                {
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_sql_referencing_entities"))
                        return BuiltInRowsetSource(context, ParseSqlReferencingEntities(context, objectName.ToString()));
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_sql_referenced_entities"))
                        return BuiltInRowsetSource(context, ParseSqlReferencedEntities(context, objectName.ToString()));
                }

                // Linked-server fork: four-part `server.db.schema.t` routes
                // to the matching <see cref="LinkedServer"/>'s remote
                // <see cref="HeapTable"/>. The lateral plan opens a fresh
                // remote connection at execute time and issues
                // `SELECT * FROM [db].[schema].[t]` through the remote's
                // full pipeline. An unknown leading segment falls through
                // to the standard Msg 208 path (this branch returns false
                // only when the remote table isn't found; the 4-part-name
                // case never falls through to CTE / view / TVF / heap
                // lookups since those are 1- to 3-part forms).
                if (objectName.Count == 4)
                {
                    if (!context.Batch.TryResolveLinkedServerTable(objectName, out var linkedServer, out var remoteTable, out var remoteDbName, out var remoteSchemaName))
                        throw SimulatedSqlException.InvalidObjectName(objectName);
                    var linkedColumnNames = new string[remoteTable.Columns.Length];
                    for (var ci = 0; ci < linkedColumnNames.Length; ci++)
                        linkedColumnNames[ci] = remoteTable.Columns[ci].Name;
                    var linkedAlias = ConsumeOptionalAlias(context);
                    _ = ParseOptionalTableHints(context);
                    return new FromSource(
                        qualifier: linkedAlias ?? remoteTable.Name,
                        columnNames: linkedColumnNames,
                        columns: remoteTable.Columns,
                        storedSchema: remoteTable.Columns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: Selection.ForLinkedServer(linkedServer, remoteDbName, remoteSchemaName, remoteTable.Name, remoteTable.Columns));
                }

                if (objectName.Count == 1
                    && context.CteBindings is { } cteBindings
                    && cteBindings.TryGetValue(objectName.Leaf, out var cteBinding))
                {
                    // Recursive-part self-reference: the body parser has
                    // captured the anchor's schema and toggled
                    // IsRecursivePartParse. The FromSource pulls rows from
                    // the binding's per-iteration rowset slot, which the
                    // recursive Selection rebinds between iterations.
                    if (cteBinding.IsRecursivePartParse && cteBinding.Schema is { } recursiveSchema)
                    {
                        cteBinding.SelfReferenceCountInCurrentBranch++;
                        var recursiveColumns = new HeapColumn[recursiveSchema.Length];
                        for (var ci = 0; ci < recursiveColumns.Length; ci++)
                            recursiveColumns[ci] = new HeapColumn(string.Empty, recursiveSchema[ci], maxLength: null, nullable: true);
                        var recursiveAlias = ConsumeOptionalAlias(context);
                        return new FromSource(
                            qualifier: recursiveAlias ?? cteBinding.Name,
                            columnNames: cteBinding.ColumnNames,
                            columns: recursiveColumns,
                            storedSchema: recursiveColumns,
                            storageOrdinals: null,
                            lobStore: null,
                            rows: SelfReferenceRows(cteBinding));
                    }

                    if (cteBinding.Plan is null)
                        throw SimulatedSqlException.RecursiveCteMissingUnionAll(cteBinding.Name);

                    // A CTE body parses before the referencing statement's sink
                    // exists, so it owns its reads and they reach no check site
                    // of their own. Folding them into the statement's list is
                    // what puts them through the ordinary execution-time SELECT
                    // check — real checks a CTE body's reads against the caller
                    // like any other source (probe-confirmed, Msg 229 naming the
                    // base object).
                    FoldSecurables(context, cteBinding.Plan);

                    var cteColumns = new HeapColumn[cteBinding.Plan.Schema.Length];
                    for (var ci = 0; ci < cteColumns.Length; ci++)
                        cteColumns[ci] = new HeapColumn(string.Empty, cteBinding.Plan.Schema[ci], maxLength: null, nullable: true);

                    var cteAlias = ConsumeOptionalAlias(context);

                    return new FromSource(
                        qualifier: cteAlias ?? cteBinding.Name,
                        columnNames: cteBinding.ColumnNames,
                        columns: cteColumns,
                        storedSchema: cteColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: cteBinding.Plan,
                        lateralIsQueryBody: true);
                }

                // Catalog views (sys.tables / sys.objects / sys.schemas)
                // route to a virtual FromSource whose rows project from live
                // metadata at execution time — wrapped as a LateralPlan so
                // each Execute re-runs the generator and picks up CREATE /
                // DROP changes from earlier in the same batch.
                if (context.Batch.TryResolveCatalogView(objectName, out var catalogView, out var catalogTargetDb))
                {
                    var catalogColumnNames = new string[catalogView.Columns.Length];
                    for (var ci = 0; ci < catalogColumnNames.Length; ci++)
                        catalogColumnNames[ci] = catalogView.Columns[ci].Name;
                    // A system table-valued function is registered as a catalog
                    // view but carries an empty argument list at the call site
                    // (e.g. `sys.fn_helpcollations()`). Consume the `()` so the
                    // cursor lands on the closing `)` for ConsumeOptionalAlias,
                    // mirroring the user-TVF branch below; without it the `)` is
                    // stranded and a draining dispatch loop re-parses it into a
                    // spurious "Incorrect syntax near ')'". A non-empty leading
                    // `(` is an old-style table hint (e.g. `(NOLOCK)`) — left for
                    // ParseOptionalTableHints, and a plain catalog view
                    // (`sys.tables`) has no parens at all.
                    var afterCatalogName = context.SaveCheckpoint();
                    context.MoveNextOptional();
                    if (context.Token is Operator { Character: '(' }
                        && context.GetNextOptional() is Operator { Character: ')' })
                    {
                        // Cursor now rests on the closing `)`.
                    }
                    else
                    {
                        context.RestoreCheckpoint(afterCatalogName);
                    }

                    var catalogAlias = ConsumeOptionalAlias(context);
                    // Catalog views are read-only metadata so the hints have no
                    // semantic effect, but the name-validation gate must still
                    // run (probe-confirmed against SQL Server 2025: Msg 321 on
                    // an unrecognized hint name applies to sys.* targets too).
                    _ = ParseOptionalTableHints(context);
                    return new FromSource(
                        qualifier: catalogAlias ?? catalogView.Name,
                        columnNames: catalogColumnNames,
                        columns: catalogView.Columns,
                        storedSchema: catalogView.Columns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: Selection.ForCatalogView(catalogView, catalogTargetDb),
                        materializeOnce: true,
                        backingCatalogView: catalogView,
                        backingCatalogDatabase: catalogTargetDb);
                }

                // View resolution: `FROM schema.view [alias]` or
                // `FROM view [alias]` (unqualified). Routes before table
                // lookup so a view with the same name as a table (rare —
                // collisions raise Msg 2714 at CREATE) wins; in practice
                // the name namespace is shared, so either resolver finds
                // the right object. Views are re-parsed and executed per
                // call via Selection.ForView (a lateral plan); the body's
                // own FROM sources resolve in a child batch isolated from
                // the caller's parser cursor.
                if (context.Batch.TryResolveView(objectName, out var resolvedView))
                {
                    var viewColumnNames = new string[resolvedView.OutputColumns.Length];
                    for (var ci = 0; ci < viewColumnNames.Length; ci++)
                        viewColumnNames[ci] = resolvedView.OutputColumns[ci].Name;
                    var viewAlias = ConsumeOptionalAlias(context);
                    var viewHints = ParseOptionalFromSourceHints(context, viewAlias is not null, objectName.ToString());
                    // NOEXPAND reads an indexed view's materialized index
                    // rather than expanding its body, which is one of the
                    // operations real's SET-option gate covers — Msg 1934
                    // under the enclosing statement's verb (probe-confirmed
                    // for both a QUOTED_IDENTIFIER OFF and an ANSI_WARNINGS
                    // OFF session). A plain reference to the same view is
                    // never gated, and create-time body binding is exempt the
                    // way the write and XML-method gates are. The simulator
                    // always expands, so the hint has no other effect.
                    if (viewHints.NoExpand
                        && resolvedView.Indexes.Count > 0
                        && !context.Batch.CreateTimeBinding
                        && Simulation.IncorrectSetOptionNames(context) is { } noExpandSetOptions)
                    {
                        throw SimulatedSqlException.IncorrectSetOptions(context.Batch.CurrentStatement.StatementVerb, noExpandSetOptions);
                    }
                    var viewSynonym = RecordSecurableRead(context, resolvedView, objectName);
                    return new FromSource(
                        qualifier: viewAlias ?? resolvedView.Name,
                        columnNames: viewColumnNames,
                        columns: resolvedView.OutputColumns,
                        storedSchema: resolvedView.OutputColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: Selection.ForView(resolvedView),
                        backingView: resolvedView,
                        viaSynonym: viewSynonym,
                        autoElementName: viewAlias ?? objectName.ToString());
                }

                // TVF call from FROM clause: `FROM schema.fn(args) [alias]`.
                // Detected when the resolved function is an inline or
                // multi-statement TVF AND `(` follows the name (cursor is on
                // the name leaf post-ParseObjectName; peek the next token via
                // a checkpoint). A ScalarFunction here falls through to the
                // table-lookup branch and surfaces Msg 208 (probe-confirmed:
                // real SQL Server treats `FROM dbo.scalar_fn(...)` as a
                // missing-object error, not a kind-mismatch).
                if (context.Batch.TryResolveFunction(objectName, out var function)
                    && function is InlineTableValuedFunction or MultiStatementTableValuedFunction)
                {
                    var checkpoint = context.SaveCheckpoint();
                    context.MoveNextOptional();
                    if (context.Token is Operator { Character: '(' })
                    {
                        context.MoveNextRequired();
                        var tvfArgs = Expressions.UserFunctionCall.ParseFunctionArguments(function, context);
                        // ParseFunctionArguments leaves the cursor on the closing `)`.
                        var tvfAlias = ConsumeOptionalAlias(context);
                        var outputColumns = function is InlineTableValuedFunction inline
                            ? inline.OutputColumns
                            : ((MultiStatementTableValuedFunction)function).OutputColumns;
                        _ = RecordSecurableRead(context, function, objectName);
                        var lateralPlan = function is InlineTableValuedFunction inlineTvf
                            ? Selection.ForInlineTvf(inlineTvf, tvfArgs)
                            : Selection.ForMultiStatementTvf((MultiStatementTableValuedFunction)function, tvfArgs);
                        return new FromSource(
                            qualifier: tvfAlias ?? function.Name,
                            columnNames: [.. outputColumns.Select(c => c.Name)],
                            columns: outputColumns,
                            storedSchema: outputColumns,
                            storageOrdinals: null,
                            lobStore: null,
                            rows: [],
                            lateralPlan: lateralPlan);
                    }
                    context.RestoreCheckpoint(checkpoint);
                }

                if (!context.Batch.TryResolveTable(objectName, out var heapTable))
                {
                    // Skip mode: real SQL Server defers name binding, so a table
                    // referenced by an un-taken branch compiles and is discarded.
                    // Substitute a placeholder source so the rest of the
                    // statement (including any trailing ELSE / END the recovery
                    // scan would otherwise orphan) parses to completion. Consume
                    // an optional TVF-style argument group, alias, and hints so
                    // the cursor lands past the source. The statement never
                    // executes, so the placeholder's shape is immaterial.
                    if (context.Batch.IsSkipping)
                    {
                        var probe = context.SaveCheckpoint();
                        context.MoveNextOptional();
                        if (context.Token is Operator { Character: '(' })
                            SkipBalancedParens(context);
                        else
                            context.RestoreCheckpoint(probe);
                        var placeholderAlias = ConsumeOptionalAlias(context);
                        _ = ParseOptionalTableHints(context);
                        return FromSource.DeferredPlaceholder(placeholderAlias ?? objectName.Leaf);
                    }
                    throw context.Batch.UnresolvableObjectName(objectName);
                }

                // A disabled clustered index makes the table unreachable on real,
                // so a query naming it fails before anything else about the source
                // is considered.
                Simulation.RejectDisabledClusteredIndex(heapTable);

                var heapColumnNames = new string[heapTable.Columns.Length];
                for (var ci = 0; ci < heapColumnNames.Length; ci++)
                    heapColumnNames[ci] = heapTable.Columns[ci].Name;

                // Optional FOR SYSTEM_TIME clause between the table name and
                // any alias. Only legal on a system-versioned parent; a
                // non-temporal target is Msg 13544.
                var temporalRowSource = ParseOptionalForSystemTime(context, heapTable);

                // FOR SYSTEM_TIME leaves the cursor at the post-clause lookahead
                // token (its ALL / AS-OF-expr parse already advanced past the
                // clause), whereas the no-clause path leaves it on the table
                // name leaf. ConsumeOptionalAlias advances before checking, so
                // after a temporal clause the current token is already the alias
                // candidate — consume it in place; otherwise use the advancing
                // form. Without this the token after a trailing WHERE / alias is
                // stranded and a draining consumer re-parses it into a spurious
                // syntax error.
                var heapAlias = temporalRowSource is null
                    ? ConsumeOptionalAlias(context)
                    : ConsumeOptionalAliasAtCurrent(context);
                ParseOptionalTableSample(context);
                var heapHints = ParseOptionalFromSourceHints(context, heapAlias is not null, objectName.ToString());
                ValidateIndexHintArguments(context.Batch.CurrentDatabase.Collation, heapHints, heapTable, $"{objectName.ImmediateQualifier ?? Database.DefaultSchemaName}.{heapTable.Name}");
                ValidateForceSeekColumns(context.Batch.CurrentDatabase.Collation, heapHints, heapTable);
                // Phase 1b: acquire table-level IS/IX/S/X (based on hints +
                // isolation level) and capture the per-row plan. Temporal
                // FOR SYSTEM_TIME sources bypass the per-row probe (they
                // materialize through a separate path that doesn't expose
                // RIDs).
                var heapPlan = context.Batch.AcquireDataLockIfApplicable(heapTable, heapHints, isWrite: false);
                var heapSynonym = RecordSecurableRead(context, heapTable, objectName);
                var heapQualifier = heapAlias ?? objectName.Leaf;
                var heapRows = temporalRowSource
                    ?? (heapPlan.NoLockReader
                        ? heapTable.Rows
                        : BatchContext.WrapWithRowConflictChecks(heapTable, context.Batch, heapPlan));

                return new FromSource(
                    qualifier: heapQualifier,
                    columnNames: heapColumnNames,
                    columns: heapTable.Columns,
                    storedSchema: heapTable.StoredColumns,
                    storageOrdinals: heapTable.StorageOrdinals,
                    lobStore: heapTable.Heap,
                    rows: heapRows,
                    backingTable: heapTable,
                    heapPlan: temporalRowSource is null ? heapPlan : null,
                    viaSynonym: heapSynonym,
                    autoElementName: heapAlias ?? objectName.ToString(),
                    writtenObjectName: objectName.ToString());

            // Table-variable source: <c>FROM @t [alias]</c>. Routes through
            // BatchContext.TableVariables instead of the regular schema dict;
            // missing @t raises Msg 1087 (distinct from regular tables'
            // Msg 208) since the user's spelling tells us they meant a
            // table variable, not a missing table.
            case AtPrefixedString:
                var tvName = BatchContext.ParseObjectName(context, acceptTableVariable: true);
                if (!context.Batch.TryResolveTable(tvName, out var tvTable))
                    throw SimulatedSqlException.MustDeclareTableVariable(tvName.Leaf);
                var tvColumnNames = new string[tvTable.Columns.Length];
                for (var ci = 0; ci < tvColumnNames.Length; ci++)
                    tvColumnNames[ci] = tvTable.Columns[ci].Name;
                var tvAlias = ConsumeOptionalAlias(context);
                _ = ParseOptionalTableHints(context);
                return new FromSource(
                    qualifier: tvAlias ?? tvName.Leaf,
                    columnNames: tvColumnNames,
                    columns: tvTable.Columns,
                    storedSchema: tvTable.StoredColumns,
                    storageOrdinals: tvTable.StorageOrdinals,
                    lobStore: tvTable.Heap,
                    rows: tvTable.Rows,
                    backingTable: tvTable,
                    writtenObjectName: tvName.Leaf);

            case Operator { Character: '(' }:
                var afterOpenParen = context.GetNextRequired();

                // Table-value-constructor derived table: `(VALUES …) alias(cols)`.
                // Rides the same deferred lateral-plan seam as a derived-table
                // SELECT, so a VALUES source correlates to outer scope the same
                // way (needed for a comma-FROM VALUES referencing an outer CTE).
                if (afterOpenParen is ReservedKeyword { Keyword: Keyword.Values })
                    return ParseValuesDerivedTable(context, context.OuterTypeResolver ?? outerTypeResolver);

                if (afterOpenParen is not ReservedKeyword { Keyword: Keyword.Select })
                {
                    // A CTE prefix inside a derived table is real's Msg 156
                    // rather than the generic Msg 102 — a WITH may only
                    // precede a statement, never a parenthesized query
                    // (probe-confirmed; real follows it with Msg 319 and
                    // Msg 102, of which the simulator raises the first).
                    throw afterOpenParen is ReservedKeyword { Keyword: Keyword.With } withKeyword
                        ? SimulatedSqlException.SyntaxErrorNearKeyword(withKeyword)
                        : SimulatedSqlException.SyntaxErrorNear(context);
                }

                // Derived tables can correlate to outer scope (SQL Server
                // allows any FROM derived table to reference outer columns,
                // not just APPLY). Static parse-time correlation detection
                // misses runtime-only references (WHERE / ON predicates use
                // Run, not GetSqlType), so the safe path is to always defer
                // execution into FromSource.LateralPlan and re-run per outer
                // resolver invocation. Non-correlated derived tables pay the
                // same per-Execute cost as before (the inner plan still runs
                // once per outer Execute call, just routed through
                // lateralPlan.Execute).
                //
                // Pass through the chained outer-type-resolver so the inner
                // Parse can statically type-resolve any projection / GROUP
                // BY references that point at outer columns. Both
                // <see cref="ParserContext.OuterTypeResolver"/> (set inside
                // the WHERE / GROUP BY / HAVING parse of the enclosing
                // Selection) and the explicit <paramref name="outerTypeResolver"/>
                // chain (set when this FROM source is itself nested inside
                // a subquery) are honored.
                var derivedSelection = ParseNestedQueryRejectingNextValueFor(context, depth + 1,
                    context.OuterTypeResolver ?? outerTypeResolver);

                // Inner SELECT result rows are LOB-inline (projections never
                // emit LOB pointers because they have no destination Heap),
                // so build a HeapColumn[] schema from the SqlType[] so the
                // decoder still strips marker bytes for text/ntext/image
                // columns; lobStore is null because no chain to follow.
                var derivedColumns = new HeapColumn[derivedSelection.Schema.Length];
                for (var ci = 0; ci < derivedColumns.Length; ci++)
                    derivedColumns[ci] = new HeapColumn(string.Empty, derivedSelection.Schema[ci], maxLength: null, nullable: true);

                // A body that never closed its paren is Msg 102 naming what the
                // parse stopped on — the last token of the batch when the input
                // simply ended (probed 2026-08-05: `SELECT * FROM (SELECT 1 AS a`
                // reports `near 'a'`). Checked before the alias, whose own
                // diagnostic below assumes a closing paren was there to name.
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                // Msg 1033: a derived table is one of the five constructs the
                // message names, and its ORDER BY needs a companion TOP /
                // OFFSET / FETCH — the same test the view and CTE bodies run
                // (probe-confirmed 2026-08-06: `FROM (SELECT v FROM … ORDER BY v) d`
                // raises, and adding TOP or OFFSET clears it).
                if (derivedSelection.HasOrderBy && !derivedSelection.HasTopOrOffsetOrFetch)
                    throw SimulatedSqlException.OrderByInvalidInCte();

                // A derived table has no native name, so the alias is
                // mandatory: real reports Msg 102 near the closing ')' when
                // it's missing (probe-confirmed 2026-07-31).
                var derivedQualifier = ConsumeOptionalAlias(context)
                    ?? throw SimulatedSqlException.SyntaxErrorNear(')');
                var derivedNames = ResolveDerivedTableColumnNames(context, derivedSelection.ColumnNames, derivedQualifier);

                return new FromSource(
                    qualifier: derivedQualifier,
                    columnNames: derivedNames,
                    columns: derivedColumns,
                    storedSchema: derivedColumns,
                    storageOrdinals: null,
                    lobStore: null,
                    rows: [],
                    lateralPlan: derivedSelection,
                    lateralIsQueryBody: true);

            case ReservedKeyword { Keyword: Keyword.OpenXml }:
                // OPENXML dispatch: the pre-OPENJSON XML rowset, read over a
                // document sp_xml_preparedocument put in the session's store.
                // OPENXML is a reserved keyword, so it arrives here rather than
                // in the Name case that carries OPENJSON. ParseOpenXml consumes
                // the argument list and the optional WITH clause, leaving the
                // cursor one past the source (BuiltInRowsetSource's contract).
                return BuiltInRowsetSource(context, ParseOpenXml(context));

            case ReservedKeyword { Keyword: Keyword.OpenQuery }:
                // OPENQUERY dispatch: an ad-hoc pass-through rowset over a
                // linked server — a sibling of the four-part-name read that
                // rides the same remote-execution seam. OPENQUERY is a
                // reserved keyword, so it arrives here rather than in the
                // Name case. ParseOpenQuery enforces the
                // `( server , 'query' )` grammar, resolves the linked server
                // (Msg 7202 on miss), and discovers the result-set schema by
                // running the query once on the remote.
                {
                    var openQueryPlan = ParseOpenQuery(context);
                    var openQueryAlias = ConsumeOptionalAliasInPlace(context);
                    // A column-alias list — `OPENQUERY(...) q(c1, c2)` — is not
                    // allowed on OPENQUERY (real SQL Server: Msg 102 near the
                    // first alias identifier). The general FROM parser tolerates
                    // a trailing column-alias list by ignoring it; reject it
                    // here so the columns keep coming from the remote result set
                    // rather than being silently renamed away.
                    if (context.Token is Operator { Character: '(' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    var openQueryColumns = new HeapColumn[openQueryPlan.Schema.Length];
                    for (var ci = 0; ci < openQueryColumns.Length; ci++)
                        openQueryColumns[ci] = new HeapColumn(openQueryPlan.ColumnNames[ci], openQueryPlan.Schema[ci], maxLength: null, nullable: true);
                    return new FromSource(
                        qualifier: openQueryAlias,
                        columnNames: openQueryPlan.ColumnNames,
                        columns: openQueryColumns,
                        storedSchema: openQueryColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: openQueryPlan);
                }

            // CONTAINSTABLE / FREETEXTTABLE dispatch: the rowset forms of the
            // two full-text predicates, projecting KEY and RANK. Both names are
            // reserved keywords, so they arrive here rather than in the Name
            // case that carries OPENJSON.
            case ReservedKeyword { Keyword: Keyword.ContainsTable or Keyword.FreeTextTable } ftRowset:
                return BuiltInRowsetSource(context, ParseFullTextTable(context, ftRowset.Keyword == Keyword.FreeTextTable));

            case ReservedKeyword
            {
                Keyword: Keyword.SemanticKeyPhraseTable or Keyword.SemanticSimilarityTable
                    or Keyword.SemanticSimilarityDetailsTable
            } semanticRowset:
                throw new NotSupportedException(
                    $"Semantic search rowset functions ({semanticRowset.Keyword.ToString().ToUpperInvariant()}) are not modeled.");

            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// Wraps a built-in rowset function's synthesized plan (OPENJSON /
    /// STRING_SPLIT / GENERATE_SERIES / fn_listextendedproperty) as a FROM
    /// source: projects the plan's schema into per-column
    /// <see cref="HeapColumn"/>s (all nullable — these sources have no
    /// storage-backed constraints), consumes the optional alias, and defers
    /// execution to the plan via <see cref="FromSource.LateralPlan"/>. Entered
    /// with the cursor just past the function's closing <c>)</c> (each parser
    /// consumes through its own argument list).
    /// </summary>
    private static FromSource BuiltInRowsetSource(ParserContext context, Selection plan)
    {
        var columns = new HeapColumn[plan.Schema.Length];
        for (var ci = 0; ci < columns.Length; ci++)
            columns[ci] = new HeapColumn(plan.ColumnNames[ci], plan.Schema[ci], maxLength: null, nullable: true);
        return new FromSource(
            qualifier: ConsumeOptionalAliasInPlace(context),
            columnNames: plan.ColumnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);
    }

    /// <summary>
    /// Parses a table-value-constructor derived table:
    /// <c>(VALUES (row), (row), …) alias(col, col, …)</c>. Entered with the
    /// cursor on the <c>VALUES</c> keyword (the caller has consumed the opening
    /// <c>(</c> and matched the keyword). On return the cursor sits at the
    /// first un-consumed token after the alias's column list (WHERE / JOIN /
    /// comma / <c>)</c> / <c>;</c> / null). The alias and its column-alias list
    /// are both required — real SQL Server raises <strong>Msg 102</strong>
    /// (no alias) or <strong>Msg 8155</strong> (no column list). Per-column
    /// result types promote across rows exactly like set-op / CASE branches
    /// (<see cref="SqlType.Promote"/>); the resulting plan defers to
    /// <see cref="ForValuesConstructor"/> so a VALUES source under APPLY can
    /// correlate to the outer row.
    /// </summary>
    private static FromSource ParseValuesDerivedTable(ParserContext context, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        // ParseValuesTuples enters on VALUES and leaves the cursor on the token
        // after the last tuple's ')', which must be the (VALUES …) wrapper's
        // closing ')'.
        var tuples = Simulation.ParseValuesTuples(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Every row must have the same column count (Msg 10709).
        var arity = tuples[0].Length;
        for (var i = 1; i < tuples.Count; i++)
        {
            if (tuples[i].Length != arity)
                throw SimulatedSqlException.TableValueConstructorRowArityMismatch();
        }

        // ConsumeOptionalAlias expects the cursor on the closing ')'; it
        // advances past it and consumes `AS alias` / bare `alias`. A VALUES
        // derived table requires the alias (Msg 102 near ')' otherwise).
        var alias = ConsumeOptionalAlias(context)
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        // The column-alias list is mandatory (Msg 8155 when absent).
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.NoColumnNameSpecified(1, alias);
        var columnNames = ParseColumnAliasList(context);

        // Msg 8158 (rows wider than the list) / Msg 8159 (rows narrower).
        if (arity > columnNames.Length)
            throw SimulatedSqlException.HasMoreColumnsThanColumnList(alias);
        if (arity < columnNames.Length)
            throw SimulatedSqlException.HasFewerColumnsThanColumnList(alias);

        // Per-column type promotion across every row's cell — mirrors the
        // set-op / CASE joint-envelope rule. Correlated cell references
        // (a VALUES source under APPLY) resolve through the chained outer
        // type resolver.
        SqlType TypeResolver(MultiPartName name) =>
            outerTypeResolver is not null
                ? outerTypeResolver(name)
                : throw SimulatedSqlException.InvalidColumnName(name);
        var schema = new SqlType[arity];
        for (var c = 0; c < arity; c++)
        {
            var colType = tuples[0][c].GetSqlType(context.Batch, TypeResolver);
            for (var i = 1; i < tuples.Count; i++)
                colType = SqlType.Promote(colType, tuples[i][c].GetSqlType(context.Batch, TypeResolver));
            schema[c] = colType;
        }

        // Per-column nullability = OR across every row's cell: a VALUES column
        // is NOT NULL only when no row supplies a nullable expression there, so
        // `(VALUES('a'),('b')) v(n)` reports n NOT NULL while `(VALUES(1),(NULL))`
        // stays nullable (probe-confirmed against SQL Server 2025; the outer
        // single-source projection surfaces it as the COLMETADATA fNullable flag
        // go-mssqldb / tedious expose). A correlated cell reference resolves
        // nullable — its outer-column nullability isn't threaded here.
        var cellNullability = new NullabilityContext(context.Batch, static _ => true, TypeResolver);
        var columns = new HeapColumn[arity];
        for (var c = 0; c < arity; c++)
        {
            var nullable = false;
            for (var i = 0; i < tuples.Count && !nullable; i++)
                nullable = tuples[i][c].ResultIsNullable(cellNullability);
            columns[c] = new HeapColumn(columnNames[c], schema[c], maxLength: null, nullable: nullable);
        }

        return new FromSource(
            qualifier: alias,
            columnNames: columnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: ForValuesConstructor(schema, columnNames, tuples));
    }

    /// <summary>
    /// Whether <paramref name="token"/> is one the projection switch would
    /// read as the start of an element. Used to reject a second value where
    /// only a separator may follow — the keyword cases are handled separately
    /// because most of them legitimately end the list.
    /// </summary>
    private static bool StartsProjectionElement(Token? token) =>
        token is Name or Numeric or Literal or AtPrefixedString;

    /// <summary>
    /// Whether <paramref name="keyword"/> can open a projection element.
    /// The reserved words that can are the function-call heads (<c>LEFT</c> /
    /// <c>RIGHT</c> / <c>CONVERT</c> / <c>TRY_CONVERT</c> / <c>COALESCE</c> /
    /// <c>NULLIF</c>), <c>CASE</c>, the parens-less niladic constants, and the
    /// <c>NULL</c> literal — the same set the projection switch routes to
    /// <see cref="Expression.Parse"/>. Everything else is a clause or
    /// statement keyword that can only terminate a list, never begin one.
    /// </summary>
    private static bool CanBeginProjectionElement(ReservedKeyword keyword) =>
        keyword.Keyword is Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert
            or Keyword.Coalesce or Keyword.NullIf or Keyword.Case or Keyword.Null
            or Keyword.Current_Timestamp or Keyword.Current_Date or Keyword.Current_User
            or Keyword.Session_User or Keyword.System_user or Keyword.User
            or Keyword.Distinct or Keyword.All or Keyword.Top;

    /// <summary>
    /// Applies a derived table's optional column-alias list —
    /// <c>(SELECT …) s(a, b)</c> — which renames every output column,
    /// overriding whatever the inner projection called them. Entered with the
    /// cursor wherever <see cref="ConsumeOptionalAlias"/> left it; consumes
    /// the list when one is present and returns the effective names.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025: a list shorter than the
    /// projection is <strong>Msg 8158</strong>, longer is <strong>Msg
    /// 8159</strong>, and a repeated name is <strong>Msg 8156</strong>.
    /// With no list, every column must already have a name — an unnamed one is
    /// <strong>Msg 8155</strong>, reported once per unnamed column.
    /// </remarks>
    private static string[] ResolveDerivedTableColumnNames(ParserContext context, string[] projectedNames, string? qualifier)
    {
        if (qualifier is null)
            return projectedNames;

        if (context.Token is not Operator { Character: '(' })
        {
            List<int>? unnamed = null;
            for (var i = 0; i < projectedNames.Length; i++)
            {
                if (string.IsNullOrEmpty(projectedNames[i]))
                    (unnamed ??= []).Add(i + 1);
            }
            return unnamed is null
                ? projectedNames
                : throw SimulatedSqlException.NoColumnNamesSpecified(unnamed, qualifier);
        }

        var renamed = ParseColumnAliasList(context);
        if (projectedNames.Length > renamed.Length)
            throw SimulatedSqlException.HasMoreColumnsThanColumnList(qualifier);
        if (projectedNames.Length < renamed.Length)
            throw SimulatedSqlException.HasFewerColumnsThanColumnList(qualifier);

        for (var i = 0; i < renamed.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (Collation.Baseline.Equals(renamed[i], renamed[j]))
                    throw SimulatedSqlException.ColumnSpecifiedMultipleTimes(renamed[i], qualifier);
            }
        }
        return renamed;
    }

    /// <summary>
    /// Parses a parenthesized column-alias list <c>(col, col, …)</c> — the
    /// name list a VALUES derived table (or any aliased rowset) attaches to
    /// rename its columns. Entered with the cursor on the opening <c>(</c>;
    /// on return the cursor sits at the first token after the closing
    /// <c>)</c>. Each name is an identifier (bare or bracketed); anything
    /// else raises <strong>Msg 102</strong>.
    /// </summary>
    private static string[] ParseColumnAliasList(ParserContext context)
    {
        var names = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name columnName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(columnName.Value);
            var separator = context.GetNextRequired();
            if (separator is Operator { Character: ')' })
                break;
            if (separator is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return [.. names];
    }

    /// <summary>
    /// If <see cref="ParserContext.Token"/> is one of the JOIN-introducing
    /// keywords (<c>INNER</c> / <c>LEFT</c> / <c>RIGHT</c> / <c>FULL</c> /
    /// <c>CROSS</c> / bare <c>JOIN</c>), consumes it (plus an optional
    /// <c>OUTER</c> after LEFT/RIGHT/FULL and the required <c>JOIN</c>
    /// keyword) and returns the join kind. Returns false otherwise (no
    /// advancement).
    /// </summary>
    private static bool TryParseJoinKeyword(ParserContext context, out JoinKind kind)
    {
        kind = JoinKind.Inner;
        if (context.Token is not ReservedKeyword keyword)
            return false;

        switch (keyword.Keyword)
        {
            case Keyword.Inner:
                context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                kind = JoinKind.Inner;
                return true;

            case Keyword.Join:
                kind = JoinKind.Inner;
                return true;

            case Keyword.Left:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.RecursiveBranchConstructs.OuterJoin = true;
                kind = JoinKind.Left;
                return true;

            case Keyword.Right:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.RecursiveBranchConstructs.OuterJoin = true;
                kind = JoinKind.Right;
                return true;

            case Keyword.Full:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.RecursiveBranchConstructs.OuterJoin = true;
                kind = JoinKind.Full;
                return true;

            case Keyword.Cross:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Join })
                {
                    kind = JoinKind.Cross;
                    return true;
                }
                if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Apply })
                {
                    kind = JoinKind.CrossApply;
                    return true;
                }
                throw SimulatedSqlException.SyntaxErrorNear(context);

            // OUTER as a leading keyword introduces OUTER APPLY (the
            // LEFT/RIGHT/FULL OUTER forms consume OUTER inside their own
            // cases above). The cursor is on OUTER; advance and require APPLY.
            case Keyword.Outer:
                context.MoveNextRequired();
                if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Apply })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                kind = JoinKind.OuterApply;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// After all FROM sources are parsed, sets
    /// <see cref="ParserContext.OuterTypeResolver"/> to a chained resolver
    /// (this scope's sources, falling through to the prior outer) for the
    /// duration of the WHERE / GROUP BY / HAVING / ORDER BY parse.
    /// Subqueries that appear inside those clauses pick up the chained
    /// resolver and pass it as their own outer resolver to
    /// <see cref="Parse"/>.
    /// </summary>
    private static void ConsumeWhereOrderByWithOuterScope(
        ParserContext context,
        FromClause fromClause,
        FromSource[] sources,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        bool allowOrderBy,
        uint depth)
    {
        SqlType MyResolver(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

        var saved = context.OuterTypeResolver;
        var savedScopeSources = context.ScopeSources;
        context.OuterTypeResolver = MyResolver;
        // A WHERE-clause CONTAINS / FREETEXT binds its column specification
        // against these same sources.
        context.ScopeSources = sources;
        try
        {
            ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy, depth);
        }
        finally
        {
            context.OuterTypeResolver = saved;
            context.ScopeSources = savedScopeSources;
        }
    }

    /// <summary>
    /// Consumes an optional <c>AS alias</c> after a FROM source. Returns the
    /// alias text if present, null otherwise. On entry, the FROM source
    /// (table name or derived-table closing <c>)</c>) is the current token;
    /// this advances past it, optionally past <c>AS alias</c> or a bare
    /// <c>alias</c> (the implicit alias form), and leaves the cursor at
    /// the next un-consumed lookahead position (typically WHERE / GROUP /
    /// HAVING / ORDER / JOIN keywords / ; / null).
    /// </summary>
    internal static string? ConsumeOptionalAlias(ParserContext context)
    {
        var nextToken = context.GetNextOptional();
        if (nextToken is ReservedKeyword { Keyword: Keyword.As })
        {
            var alias = context.GetNextRequired<Name>().Value;
            context.MoveNextOptional();
            return alias;
        }
        // Bare-Name alias form (without the AS keyword): "FROM t a JOIN ..."
        // SQL Server accepts this as an alias — except a `WINDOW <name> AS (`
        // clause head, which is not an alias (WINDOW is otherwise a valid alias).
        if (nextToken is Name aliasName && !IsWindowClauseAhead(context))
        {
            context.MoveNextOptional();
            return aliasName.Value;
        }
        return null;
    }

    /// <summary>
    /// Variant of <see cref="ConsumeOptionalAlias"/> for callers whose FROM
    /// source has already advanced the cursor to the post-source lookahead
    /// token (e.g. after a FOR SYSTEM_TIME clause): the current token is the
    /// alias candidate, so this checks it in place rather than advancing first.
    /// Leaves the cursor at the next un-consumed lookahead position, matching
    /// <see cref="ConsumeOptionalAlias"/>'s post-condition.
    /// </summary>
    internal static string? ConsumeOptionalAliasAtCurrent(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
        {
            var alias = context.GetNextRequired<Name>().Value;
            context.MoveNextOptional();
            return alias;
        }
        if (context.Token is Name aliasName && !IsWindowClauseAhead(context))
        {
            context.MoveNextOptional();
            return aliasName.Value;
        }
        return null;
    }

    /// <summary>
    /// Yields the CTE binding's current iteration rowset to a recursive
    /// branch's self-reference FromSource. The runtime
    /// <see cref="CteBinding.CurrentIterationRows"/> slot is rebound by
    /// <see cref="FromRecursiveCte"/> between iterations, so each
    /// enumerator created here pulls the per-iteration rowset captured at
    /// iterator-start time.
    /// </summary>
    private static IEnumerable<byte[]> SelfReferenceRows(CteBinding binding)
    {
        var rows = binding.CurrentIterationRows;
        if (rows is null)
            yield break;
        foreach (var row in rows)
            yield return row;
    }

    /// <summary>
    /// Reads zero or more WHERE clauses, an optional GROUP BY, an optional
    /// HAVING, and an optional ORDER BY — in that order, matching SQL Server's
    /// grammar. Starts with <see cref="ParserContext.Token"/> already
    /// positioned at the first lookahead token (e.g. WHERE, GROUP, HAVING,
    /// ORDER, ;, or null). On return, <see cref="ParserContext.Token"/> is
    /// the first token after the last consumed clause (typically ;, ), or
    /// null).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ParserContext.Token"/> directly between clauses
    /// instead of advancing with <see cref="ParserContext.GetNextOptional"/>
    /// — sub-Parse helpers leave Token at the first un-consumed token per
    /// the lookahead contract, and an extra advance here would silently swallow
    /// the next clause's opening keyword.
    /// </remarks>
    private static void ConsumeWhereAndOrderBy(ParserContext context, FromClause fromClause, bool allowOrderBy, uint depth)
    {
        // A parenthesized INSERT source's own query may not carry an ORDER BY:
        // real refuses it there even with the TOP that would license one in a
        // derived table, and does so as Msg 156 on the keyword
        // (probe-confirmed 2026-08-06). Matched on depth so a derived table or
        // subquery nested inside the source keeps the ordinary rules.
        if (depth == context.ParenthesizedInsertSourceDepth
            && context.Token is ReservedKeyword { Keyword: Keyword.Order } orderKeyword)
        {
            throw SimulatedSqlException.SyntaxErrorNearKeyword(orderKeyword);
        }

        // WHERE / GROUP BY / HAVING reject windowed functions (Msg 4108) and
        // NEXT VALUE FOR (Msg 11720). Toggle the parser-context flags for the
        // duration of those parses; ORDER BY (which DOES allow windows but
        // rejects NEXT VALUE FOR) handled separately below.
        var savedAllowsWindows = context.AllowsWindowExpressions;
        var savedRejectNextValueFor = context.NextValueForRejection;
        context.AllowsWindowExpressions = false;
        _ = context.EnterNextValueForScope(NextValueForScope.Clause);
        try
        {
            while (context.Token is ReservedKeyword { Keyword: Keyword.Where })
            {
                fromClause.Excluders.Add(BooleanExpression.SimplifyForFilter(
                    BooleanExpression.Parse(context.MoveNextRequiredReturnSelf()), context));
            }

            if (context.Token is ReservedKeyword { Keyword: Keyword.Group })
            {
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.RecursiveBranchConstructs.GroupingOrAggregate = true;
                ParseGroupByList(context, fromClause);
            }

            if (context.Token is ReservedKeyword { Keyword: Keyword.Having })
            {
                context.RecursiveBranchConstructs.GroupingOrAggregate = true;
                // A HAVING settles a comparison against a folded-NULL constant
                // the way a WHERE doesn't — see SettleFoldedNullComparisons.
                fromClause.Having = BooleanExpression.SimplifyForFilter(
                    BooleanExpression.Parse(context.MoveNextRequiredReturnSelf()).SettleFoldedNullComparisons(context), context);
            }
        }
        finally
        {
            context.AllowsWindowExpressions = savedAllowsWindows;
            context.NextValueForRejection = savedRejectNextValueFor;
        }

        // Optional trailing WINDOW clause (SQL Server 2022+), between HAVING and
        // ORDER BY: `WINDOW name AS (<over-body>) [, name AS (…)]*`. Defines
        // named windows that bare `OVER w` projection references resolve to.
        // WINDOW is contextual (usable as an identifier / table alias), so it is
        // recognized here only in the clause shape (`WINDOW <name> AS (`).
        if (IsWindowClauseAhead(context))
            ParseWindowClause(context);

        // Skip ORDER BY when this branch is part of a set-op chain — the
        // top-level driver consumes it after combining branches and applies
        // the sort to the combined result. Per SQL Server, per-branch
        // ORDER BY is rejected (Msg 156).
        if (allowOrderBy && context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            // ORDER BY rejects NEXT VALUE FOR (Msg 11720), but allows windowed
            // functions. Toggle just the sequence flag for the duration.
            _ = context.EnterNextValueForScope(NextValueForScope.Clause);
            try
            {
                ParseOrderByItems(context, fromClause.OrderBy);
            }
            finally
            {
                context.NextValueForRejection = savedRejectNextValueFor;
            }
            ConsumeOffsetFetch(context, fromClause);
        }

        // All `OVER w` references (projection and ORDER BY) and the WINDOW
        // definitions are now parsed — bind each pending reference.
        ResolvePendingNamedWindows(context);

        // Every window's frame and ORDER BY are final here, which is what the
        // RANGE-frame LOB gate (Msg 8728) needs to read.
        Expressions.WindowExpression.ValidateRangeFrameOrderBy(context);
    }

    /// <summary>
    /// Returns true when the cursor sits on a <c>WINDOW &lt;name&gt; AS (</c>
    /// clause head (SQL Server 2022+). WINDOW is contextual — it may equally be
    /// a table alias or column name — so it counts as the clause only in that
    /// exact shape. Leaves the cursor unchanged.
    /// </summary>
    private static bool IsWindowClauseAhead(ParserContext context)
    {
        if (context.Token is not Name windowToken
            || !context.Batch.CurrentDatabase.Collation.Equals(windowToken.Value, "WINDOW"))
        {
            return false;
        }
        var checkpoint = context.SaveCheckpoint();
        var nameToken = context.GetNextOptional();
        var asToken = context.GetNextOptional();
        var parenToken = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return nameToken is Name
            && asToken is ReservedKeyword { Keyword: Keyword.As }
            && parenToken is Operator { Character: '(' };
    }

    /// <summary>
    /// Parses a <c>WINDOW name AS (&lt;over-body&gt;) [, …]</c> clause (cursor
    /// on the WINDOW identifier) into <see cref="ParserContext.NamedWindowDefinitions"/>.
    /// Leaves the cursor on the next un-consumed lookahead token.
    /// </summary>
    private static void ParseWindowClause(ParserContext context)
    {
        do
        {
            if (context.GetNextRequired() is not Name nameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            // A definition may carry a frame with no ORDER BY of its own — the
            // reference that resolves it can supply the ordering — so the
            // frame-needs-ORDER-BY gate waits until the merge.
            var body = Expressions.WindowExpression.ParseWindowBody(context, deferFrameOrderByCheck: true, allowWindowReference: true);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (TryFindWindowDefinition(context, nameToken.Value, out _))
                throw SimulatedSqlException.DuplicateWindowName();
            context.NamedWindowDefinitions.Add((nameToken.Value, body));
            context.MoveNextOptional();
        } while (context.Token is Operator { Character: ',' });
    }

    /// <summary>
    /// Binds each pending <c>OVER w</c> / <c>OVER (w …)</c> reference to its
    /// named-window definition, then clears the query-block's pending /
    /// definition state. An unresolved name raises Msg 5362 ("Window 'w' is
    /// undefined.").
    /// </summary>
    private static void ResolvePendingNamedWindows(ParserContext context)
    {
        if (context.PendingNamedWindows.Count == 0)
            return;
        foreach (var (window, reference) in context.PendingNamedWindows)
            window.ApplyNamedWindow(MergeWindowReference(context, reference, []));
        context.PendingNamedWindows.Clear();
        context.NamedWindowDefinitions.Clear();
    }

    /// <summary>
    /// Looks a <c>WINDOW</c>-clause definition up by name under the database
    /// collation — window names are identifiers, so a case-insensitive
    /// collation resolves <c>OVER W</c> against <c>WINDOW w AS (…)</c>.
    /// </summary>
    private static bool TryFindWindowDefinition(ParserContext context, string name, out Expressions.WindowExpression.WindowBody body)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        foreach (var (definedName, definedBody) in context.NamedWindowDefinitions)
        {
            if (collation.Equals(definedName, name))
            {
                body = definedBody;
                return true;
            }
        }
        body = default;
        return false;
    }

    /// <summary>
    /// Folds a window body that refines a named window into a single
    /// self-contained body. A definition may itself refine another
    /// (<c>WINDOW w AS (PARTITION BY g), w2 AS (w ORDER BY id)</c>) in either
    /// written order, so the walk recurses; <paramref name="visiting"/> carries
    /// the names already being folded so a loop lands on Msg 5365 rather than
    /// recursing forever. A name absent from the clause — including a
    /// definition naming itself, which real does not put in its own scope —
    /// raises Msg 5362.
    /// </summary>
    private static Expressions.WindowExpression.WindowBody MergeWindowReference(
        ParserContext context,
        Expressions.WindowExpression.WindowBody refinement,
        List<string> visiting)
    {
        if (refinement.BaseWindowName is not { } name)
            return refinement;
        var collation = context.Batch.CurrentDatabase.Collation;
        if (visiting.Exists(pending => collation.Equals(pending, name)))
            throw SimulatedSqlException.CyclicWindowReferences();
        if (!TryFindWindowDefinition(context, name, out var definition) || collation.Equals(definition.BaseWindowName, name))
            throw SimulatedSqlException.WindowIsUndefined(name);

        visiting.Add(name);
        var resolved = MergeWindowReference(context, definition, visiting);
        visiting.RemoveAt(visiting.Count - 1);

        // Each element may be supplied by exactly one side; real reports the
        // overlap with Msg 4123 whichever element it was.
        return (refinement.PartitionBy.Length > 0 && resolved.PartitionBy.Length > 0)
            || (refinement.OrderBy.Length > 0 && resolved.OrderBy.Length > 0)
            || (refinement.Frame is not null && resolved.Frame is not null)
            ? throw SimulatedSqlException.WindowElementAlreadySpecified(resolved.Frame is not null)
            : new Expressions.WindowExpression.WindowBody(
                refinement.PartitionBy.Length > 0 ? refinement.PartitionBy : resolved.PartitionBy,
                refinement.OrderBy.Length > 0 ? refinement.OrderBy : resolved.OrderBy,
                refinement.Frame ?? resolved.Frame);
    }

    /// <summary>
    /// Parses the comma-separated GROUP BY list (entered with cursor one
    /// token before the first item — caller has just consumed <c>BY</c>;
    /// next call advances onto the item). Each item is either a regular
    /// expression, <c>ROLLUP(expr_list)</c>, <c>CUBE(expr_list)</c>, or
    /// <c>GROUPING SETS((set), (set), ...)</c>. Per-item contributions are
    /// Cartesian-combined to produce the flat <see cref="FromClause.GroupingSets"/>
    /// list. Probe-confirmed semantics: <c>GROUP BY a, ROLLUP(b, c)</c>
    /// becomes <c>[[a, b, c], [a, b], [a]]</c>. The
    /// <see cref="FromClause.AllGroupingExpressions"/> list is populated as a
    /// union (in first-seen order) for GROUPING()/GROUPING_ID() validation.
    /// </summary>
    private static void ParseGroupByList(ParserContext context, FromClause fromClause)
    {
        var itemContributions = new List<List<Expression[]>>();
        do
        {
            context.MoveNextRequired();

            // Per-item binding rules, bracketed around this item's parse so one
            // offending expression fails the statement even beside a valid one
            // (probe-confirmed 2026-07-24: `GROUP BY a, GETDATE()` raises even
            // though `a` is fine). Msg 144 takes precedence over Msg 164 — a
            // correlated-subquery item reports 144 despite referencing a local
            // column. See ParserContext.AggregatesParsed for why this counts at
            // parse time instead of walking the finished expression.
            var aggregatesBefore = context.AggregatesParsed;
            var subqueriesBefore = context.SubqueriesParsed;
            var columnsBefore = context.ColumnReferencesParsed;

            var contribution = ParseGroupByItem(context);
            itemContributions.Add(contribution);

            // Both messages are held rather than thrown: real parses the whole
            // statement before binding it, so a stray token after the clause
            // outranks them (see ParserContext.PendingGroupByBindError). The
            // ??= keeps the first offending item's message, which is what an
            // immediate throw produced.
            if (context.AggregatesParsed > aggregatesBefore || context.SubqueriesParsed > subqueriesBefore)
                context.PendingGroupByBindError ??= SimulatedSqlException.AggregateOrSubqueryInGroupBy();

            // The empty grouping set contributes no expression at all, so there
            // is nothing for Msg 164 to require a column of. Probe-confirmed
            // legal on real 2026-07-24: `GROUP BY ()`, `GROUPING SETS (())`,
            // `GROUPING SETS ((a),())` and `GROUP BY (), a` all return rows.
            var contributesAnExpression = false;
            foreach (var fragment in contribution)
                contributesAnExpression |= fragment.Length > 0;

            if (contributesAnExpression && context.ColumnReferencesParsed == columnsBefore)
                context.PendingGroupByBindError ??= SimulatedSqlException.GroupByExpressionHasNoLocalColumn();
        } while (context.Token is Operator { Character: ',' });

        // Cartesian product of per-item contributions: each combination of
        // one fragment from each item gets concatenated into one grouping
        // set. Order matters only insofar as result-row ordering follows
        // grouping-set iteration order.
        var combined = new List<List<Expression>> { new() };
        foreach (var item in itemContributions)
        {
            var next = new List<List<Expression>>(combined.Count * item.Count);
            foreach (var prefix in combined)
            {
                foreach (var fragment in item)
                {
                    var merged = new List<Expression>(prefix.Count + fragment.Length);
                    merged.AddRange(prefix);
                    merged.AddRange(fragment);
                    next.Add(merged);
                }
            }
            combined = next;
        }

        // Legacy `GROUP BY <cols> WITH ROLLUP` / `WITH CUBE` modifier —
        // equivalent to `GROUP BY ROLLUP(<cols>)` / `CUBE(<cols>)`. It applies
        // over the full (simple) column list, so the Cartesian product above is
        // a single set whose members are those columns; expand it in place.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            var modifierToken = context.GetNextRequired();
            var columns = combined.Count == 1 ? combined[0] : [.. combined.SelectMany(static s => s)];
            combined = modifierToken switch
            {
                UnquotedString { ContextualKeyword: ContextualKeyword.Rollup } => RollupExpansion(columns),
                UnquotedString { ContextualKeyword: ContextualKeyword.Cube } => CubeExpansion(columns),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextOptional();
        }

        foreach (var set in combined)
            fromClause.GroupingSets.Add([.. set]);

        // Build AllGroupingExpressions: union over all sets, preserving
        // first-seen order. Uses structural identity via reference equality
        // — same Expression instance across sets because the parser shares
        // references through fragment lists, not by re-parsing.
        var seen = new HashSet<Expression>(ReferenceEqualityComparer.Instance);
        foreach (var set in fromClause.GroupingSets)
        {
            foreach (var expr in set)
            {
                if (seen.Add(expr))
                    fromClause.AllGroupingExpressions.Add(expr);
            }
        }
    }

    /// <summary>
    /// Grouping-set expansion for <c>WITH ROLLUP</c>: the full column list, then
    /// each successively-shorter prefix, down to the empty (grand-total) set.
    /// </summary>
    private static List<List<Expression>> RollupExpansion(List<Expression> columns)
    {
        var sets = new List<List<Expression>>(columns.Count + 1);
        for (var k = columns.Count; k > 0; k--)
            sets.Add(columns[..k]);
        sets.Add([]);
        return sets;
    }

    /// <summary>
    /// Grouping-set expansion for <c>WITH CUBE</c>: every subset of the column
    /// list (all <c>2^N</c> combinations), matching <c>CUBE(...)</c>.
    /// </summary>
    private static List<List<Expression>> CubeExpansion(List<Expression> columns)
    {
        var count = 1 << columns.Count;
        var sets = new List<List<Expression>>(count);
        for (var mask = count - 1; mask >= 0; mask--)
        {
            var set = new List<Expression>(System.Numerics.BitOperations.PopCount((uint)mask));
            for (var b = 0; b < columns.Count; b++)
            {
                if ((mask & (1 << b)) != 0)
                    set.Add(columns[b]);
            }
            sets.Add(set);
        }
        return sets;
    }

    /// <summary>
    /// Parses one top-level GROUP BY item. Returns the list of fragments
    /// (each fragment a column-list) the item contributes. A plain expression
    /// contributes a single one-element fragment <c>[[expr]]</c>; a ROLLUP
    /// contributes <c>N+1</c> fragments shrinking from full prefix to empty;
    /// a CUBE contributes all <c>2^N</c> subsets; a GROUPING SETS contributes
    /// each explicit set verbatim.
    /// </summary>
    private static List<Expression[]> ParseGroupByItem(ParserContext context)
    {
        if (context.Token is UnquotedString { ContextualKeyword: var kw }
            && kw is ContextualKeyword.Rollup or ContextualKeyword.Cube or ContextualKeyword.Grouping)
        {
            switch (kw)
            {
                case ContextualKeyword.Rollup:
                    {
                        var columns = ParseParenthesizedExpressionList(context);
                        var fragments = new List<Expression[]>(columns.Length + 1);
                        for (var k = columns.Length; k > 0; k--)
                            fragments.Add(columns[..k]);
                        fragments.Add([]);
                        return fragments;
                    }
                case ContextualKeyword.Cube:
                    {
                        var columns = ParseParenthesizedExpressionList(context);
                        var count = 1 << columns.Length;
                        var fragments = new List<Expression[]>(count);
                        for (var mask = count - 1; mask >= 0; mask--)
                        {
                            var bits = System.Numerics.BitOperations.PopCount((uint)mask);
                            var fragment = new Expression[bits];
                            var w = 0;
                            for (var b = 0; b < columns.Length; b++)
                            {
                                if ((mask & (1 << b)) != 0)
                                    fragment[w++] = columns[b];
                            }
                            fragments.Add(fragment);
                        }
                        return fragments;
                    }
                case ContextualKeyword.Grouping:
                    {
                        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Sets })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is not Operator { Character: '(' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        var fragments = new List<Expression[]>();
                        context.MoveNextRequired();
                        while (true)
                        {
                            fragments.Add(ParseGroupingSetMember(context));
                            if (context.Token is Operator { Character: ',' })
                            {
                                context.MoveNextRequired();
                                continue;
                            }
                            break;
                        }
                        if (context.Token is not Operator { Character: ')' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextOptional();
                        return fragments;
                    }
            }
        }
        // `GROUP BY ()` — the empty grouping set (grand total over all rows),
        // the bare-parenthesis equivalent of `GROUPING SETS(())`. Distinguished
        // from `GROUP BY (expr)` (a parenthesized grouping key) by the `)`
        // immediately following the `(`.
        if (context.Token is Operator { Character: '(' })
        {
            var checkpoint = context.SaveCheckpoint();
            if (context.GetNextRequired() is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                return [[]];
            }
            context.RestoreCheckpoint(checkpoint);
        }
        return [[Expression.Parse(context)]];
    }

    /// <summary>
    /// Parses a parenthesized comma-separated expression list, returning the
    /// expressions as an array. Entered with cursor on the keyword preceding
    /// <c>(</c> (e.g., <c>ROLLUP</c> / <c>CUBE</c>); consumes through the
    /// closing <c>)</c>.
    /// </summary>
    private static Expression[] ParseParenthesizedExpressionList(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var list = new List<Expression> { Expression.Parse(context) };
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            list.Add(Expression.Parse(context));
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return [.. list];
    }

    /// <summary>
    /// Parses one member of a <c>GROUPING SETS(...)</c> list. A member is
    /// either a parenthesized column tuple (<c>(a, b)</c> or the empty
    /// <c>()</c>) or a bare single expression. Returns the member's column
    /// list; the empty parenthesized form returns <c>[]</c> (the grand-total
    /// grouping set).
    /// </summary>
    private static Expression[] ParseGroupingSetMember(ParserContext context)
    {
        if (context.Token is Operator { Character: '(' })
        {
            context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                return [];
            }
            var list = new List<Expression> { Expression.Parse(context) };
            while (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                list.Add(Expression.Parse(context));
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return [.. list];
        }
        return [Expression.Parse(context)];
    }

    /// <summary>
    /// Consumes the optional <c>OFFSET n ROWS [FETCH NEXT|FIRST k ROW|ROWS ONLY]</c>
    /// tail. Must be called immediately after <see cref="ParseOrderByItems"/>
    /// — SQL Server requires OFFSET/FETCH to follow ORDER BY (no ORDER BY → the
    /// OFFSET keyword is just an unexpected identifier and falls through to a
    /// generic Msg 102 syntax error). FETCH alone (without preceding OFFSET) is
    /// rejected with Msg 153 here. <c>ROW</c> and <c>ROWS</c> are interchangeable;
    /// <c>NEXT</c> and <c>FIRST</c> are interchangeable. Both counts validate at
    /// parse time — non-negativity (Msg 10742) and &gt; 0 (Msg 10744) — and
    /// resolve again per execution (see <see cref="ResolveRowCountLimit"/>).
    /// </summary>
    private static void ConsumeOffsetFetch(ParserContext context, FromClause fromClause)
    {
        // FETCH at this position with no preceding OFFSET → Msg 153.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Fetch })
            throw SimulatedSqlException.FetchInvalidUsageWithoutOffset();

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Offset })
            return;

        context.MoveNextRequired();
        var offsetExpression = Expression.Parse(context);
        _ = ResolveRowCountLimit(offsetExpression, RowLimitKind.Offset, context.Batch);
        context.RecursiveBranchConstructs.TopOrOffset = true;
        fromClause.OffsetExpression = offsetExpression;

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Row or ContextualKeyword.Rows })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Fetch })
            return;

        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Next or ContextualKeyword.First })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var fetchExpression = Expression.Parse(context);
        _ = ResolveRowCountLimit(fetchExpression, RowLimitKind.Fetch, context.Batch);
        fromClause.FetchExpression = fetchExpression;

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Row or ContextualKeyword.Rows })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Only })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
    }

    /// <summary>
    /// Reads one or more ORDER BY items (comma separated). Each item is an
    /// <see cref="Expression"/> followed by an optional <c>ASC</c>/<c>DESC</c>
    /// keyword (default ASC). A signed integer literal is recorded as an
    /// ordinal reference into the projection (validated against the projection
    /// count later, Msg 108); any other term built purely from literals is
    /// rejected with Msg 408, and a bare variable with Msg 1008.
    /// </summary>
    private static void ParseOrderByItems(ParserContext context, List<OrderBySpec> orderBy)
    {
        do
        {
            context.MoveNextRequired();
            var expr = Expression.Parse(context);

            var descending = false;
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Asc }:
                    context.MoveNextOptional();
                    break;
                case ReservedKeyword { Keyword: Keyword.Desc }:
                    descending = true;
                    context.MoveNextOptional();
                    break;
            }

            // Real's ordinal form is a *signed* integer literal, parentheses
            // included: `(1)` orders by the first column and `-1` / `-(1)`
            // report Msg 108 for position -1 (probe-confirmed), while a binary
            // arithmetic expression that folds to the same number (`2 - 1`) is
            // a constant instead.
            if (IntegerOrdinalOf(expr) is { } ordinal)
            {
                orderBy.Add(OrderBySpec.FromOrdinal(ordinal, descending));
                continue;
            }

            // Position is the 1-based index in the ORDER BY list, counted
            // before the term is added.
            if (expr.IsWrittenConstant)
                throw SimulatedSqlException.ConstantExpressionInOrderBy(orderBy.Count + 1);

            // A variable reachable through pure conversions only is real's
            // "column position" shape, so it lands on its own error rather than
            // Msg 408.
            if (IsVariableColumnPosition(expr))
                throw SimulatedSqlException.VariableInOrderByPosition(orderBy.Count + 1);

            orderBy.Add(OrderBySpec.FromExpression(expr, descending));
        }
        while (context.Token is Operator { Character: ',' });
    }

    /// <summary>
    /// The ordinal an ORDER BY term names when it is an integer literal, or
    /// null. Parentheses and unary minus are peeled — real's grammar takes a
    /// signed integer constant here — so <c>(1)</c> is ordinal 1 and <c>-1</c>
    /// is ordinal -1 (out of range, Msg 108).
    /// </summary>
    /// <summary>
    /// Whether an ORDER BY term is a variable real reads as a column position
    /// (Msg 1008): a <see cref="VariableReference"/> reachable through pure
    /// conversions only — <c>@v</c>, <c>(@v)</c>, <c>((@v))</c>,
    /// <c>CAST(@v AS int)</c>. A variable inside arithmetic sorts per row
    /// instead (probe-confirmed: <c>@v + 1</c>, <c>-@v</c>, <c>(@v) + 0</c> all
    /// order the rows).
    /// </summary>
    private static bool IsVariableColumnPosition(Expression expr)
    {
        while (expr.PureConversionOperand is { } operand)
            expr = operand;
        return expr is VariableReference;
    }

    private static int? IntegerOrdinalOf(Expression expr) => expr switch
    {
        Value { IsLiteral: true, Constant: { IsNull: false } constant } when constant.Type == SqlType.Int32 => constant.AsInt32,
        Parenthesized parenthesized => IntegerOrdinalOf(parenthesized.Wrapped),
        Negate negate => IntegerOrdinalOf(negate.Operand) is { } inner ? -inner : null,
        _ => null,
    };

    /// <summary>
    /// Builds the plan for a tableless SELECT (synthesized constant-row
    /// branch). Schema and values are computed at parse time by Running each
    /// projection expression against a throwing column resolver — tableless
    /// projections don't reference any column. WHERE excluders, by contrast,
    /// can reference outer-scope columns when this Selection is the body of a
    /// correlated subquery, so they re-evaluate at <see cref="Execute"/> time
    /// against the supplied outer resolver. <paramref name="topCount"/>, if
    /// zero, suppresses the row. DISTINCT is a no-op for a single-row
    /// result and isn't represented; <paramref name="orderBy"/> is also a
    /// no-op for sort but its presence flips <see cref="HasOrderBy"/>
    /// so the set-op chain rejects per-branch ORDER BY (Msg 156).
    /// </summary>
    private static Selection BuildSynthesizedSqlRow(BatchContext parseBatch, List<Expression> expressions, List<BooleanExpression> excluders, List<OrderBySpec> orderBy, int? topCount, int? offsetCount, int? fetchCount, bool isAssignmentOnly, MultiPartName? intoTarget, Func<MultiPartName, SqlType>? outerTypeResolver, bool containsSubquery)
    {
        // The FROM-less SELECT path bakes projection values at parse time
        // (see the Run-then-GetSqlType loop below) — replaying that closure
        // across invocations would emit the same stale NEWID / GETDATE /
        // @@TRANCOUNT / NEXT VALUE FOR result every call. Disqualify the
        // batch from plan-cache promotion.
        parseBatch.HasSessionScopedReference = true;

        var values = new SqlValue[expressions.Count];
        var schema = new SqlType[expressions.Count];
        var columnNames = new string[expressions.Count];

        // Run-then-GetSqlType: any expression whose runtime path raises a
        // type-error message with operator-name wording (e.g. <c>dt + time</c>
        // → "add operator") emits that error from Run before GetSqlType has
        // a chance to throw a Promote-side message with comparison-only
        // wording. For successful runs, GetSqlType then bridges the matched
        // branch's runtime type to the joint-promoted schema (CASE / Coalesce
        // with mixed-type branches in a FROM-less SELECT).
        // A FROM-less SELECT nested in an outer query can still reference the
        // outer row (`SELECT (SELECT t.col) FROM t` — real returns one value
        // per outer row), and such a projection cannot be baked at parse time
        // because its value changes per invocation. Detect any column
        // reference and defer those to the executor, where the outer resolver
        // is supplied; the reference-free case keeps the baked fast path
        // unchanged, so `SELECT 1` still folds at parse time.
        // A parsed subquery counts as an outer reference whether or not one is
        // written: its body is a plan of its own that the walk below can't see
        // into, and it re-reads the enclosing row on every invocation, so the
        // baked value would be both wrong and stale.
        //
        // So does a parse that isn't going to run the statement at all — an
        // un-taken branch, or a module body being bound at CREATE. The bake
        // *evaluates* the projection, which for a side-effecting built-in is a
        // side effect the statement never earned: `CREATE PROCEDURE p AS SELECT
        // NEXT VALUE FOR s` drew a value here where real leaves the sequence
        // untouched (probe-confirmed 2026-08-05 — `last_used_value` stays NULL
        // there). Deferring costs nothing, since a skipped statement yields no
        // rows for anyone to read.
        var referencesOuterColumns = containsSubquery || parseBatch.IsSkipping;
        foreach (var expression in expressions)
            expression.VisitColumnReferences(_ => referencesOuterColumns = true);

        // A FROM-less SELECT holds no sources, so every name it can't hand to an
        // enclosing scope is unbindable — which makes a *qualified* one Msg 4104
        // and an unqualified one Msg 207, the split UnresolvedNameError carries.
        // This is the path a derived table over no FROM takes, so it is what
        // reports a body that names a sibling FROM source.
        SqlType TypeResolver(MultiPartName column) =>
            outerTypeResolver is not null
                ? outerTypeResolver(column)
                : throw UnresolvedNameError([], column);

        var parseRuntime = new RuntimeContext(column => throw SimulatedSqlException.InvalidColumnName(column), parseBatch);
        for (var i = 0; i < expressions.Count; i++)
        {
            columnNames[i] = expressions[i].Name;
            if (referencesOuterColumns)
            {
                // Values come from the executor instead; only the type is
                // needed here, and Run would throw on the outer reference.
                schema[i] = expressions[i].GetSqlType(parseBatch, TypeResolver);
                continue;
            }

            // Run before GetSqlType — see the ordering note above.
            var raw = expressions[i].Run(parseRuntime);
            schema[i] = expressions[i].GetSqlType(parseBatch, TypeResolver);
            values[i] = raw.IsNull || raw.Type == schema[i] ? raw : raw.CoerceTo(schema[i]);
        }

        return new Selection(schema, columnNames,
            hasOrderBy: orderBy.Count > 0,
            hasTopOrOffsetOrFetch: topCount.HasValue || offsetCount.HasValue || fetchCount.HasValue,
            (batch, outerResolver) =>
        {
            if (topCount == 0)
                return [];
            if (offsetCount is { } offset && offset > 0)
                return [];
            if (fetchCount is { } fetch && fetch < 1)
                return [];

            SqlValue Resolve(MultiPartName name) =>
                outerResolver is not null
                    ? outerResolver(name)
                    : throw SimulatedSqlException.InvalidColumnName(name);

            foreach (var excluder in excluders)
            {
                if (excluder.Run(new RuntimeContext(Resolve, batch)) != true)
                    return [];
            }

            if (!referencesOuterColumns)
                return [RowEncoder.EncodeRow(schema, values)];

            // Deferred projection: evaluate against this invocation's outer row.
            var perCall = new SqlValue[expressions.Count];
            var runtime = new RuntimeContext(Resolve, batch);
            for (var i = 0; i < expressions.Count; i++)
            {
                var raw = expressions[i].Run(runtime);
                perCall[i] = raw.IsNull || raw.Type == schema[i] ? raw : raw.CoerceTo(schema[i]);
            }

            return [RowEncoder.EncodeRow(schema, perCall)];
        }, isAssignmentOnly,
        intoTarget,
        // FROM-less SELECT INTO: no source sources/joins to inspect, so the
        // analyzer routes through the empty-FROM branch and produces
        // dest columns with literal-derived nullability and no identity.
        destColumnSchema: intoTarget is { } target
            ? ComputeIntoDestSchema(target, expressions, schema, columnNames, [], [], parseBatch, TypeResolver)
            : null)
        {
            ProjectionExpressions = [.. expressions],
            // An empty scope, not an unknown one: as the first branch of a
            // set-op chain this projects output aliases and nothing else, so a
            // trailing ORDER BY naming anything but one of them is Msg 207 on
            // real (`select 2 as x union all select 1 order by y`).
            BranchFromSources = [],
            // No FROM clause: the AUTO serializers have no table to name a
            // level after, which is their Msg 6800 / 13600 case.
            AutoSourceNames = [],
            AutoColumnSource = NoSourceColumnBinding(expressions.Count),
            AutoColumnOrdinal = NoSourceColumnBinding(expressions.Count),
            ColumnIntegerLiteralDigits = LiteralDigitsOf(expressions),
            ColumnReportsNumeric = ColumnReportsNumericOf(expressions, schema),
            // A FROM-less projection has no sources, so column nullability is
            // the per-expression rule alone (literals NOT NULL, other
            // expressions nullable) — matching real's result metadata
            // (`select 1` → Int, not IntN). The resolver is never consulted
            // (no column can appear without a source).
            ColumnNullability = ComputeColumnNullability(expressions, [], [], parseBatch, TypeResolver),
        };
    }

    /// <summary>
    /// Expands any <see cref="StarProjection"/> markers in the projection
    /// list into per-column <see cref="Reference"/> expressions, using each
    /// FROM source's <see cref="FromSource.Qualifier"/> to disambiguate
    /// same-named columns across sources (so multi-source <c>SELECT *</c>
    /// doesn't trip Msg 209). Bare <c>*</c> emits every column from every
    /// source in source order; <c>&lt;qualifier&gt;.*</c> filters to the
    /// named source. An unbound qualifier raises Msg 4104.
    /// </summary>
    private static void ExpandStars(Collation collation, List<Expression> expressions, List<FromSource> sources)
    {
        for (var i = expressions.Count - 1; i >= 0; i--)
        {
            if (expressions[i] is not StarProjection star)
                continue;

            var expanded = new List<Expression>();
            if (star.Qualifier is null)
            {
                foreach (var source in sources)
                    AppendSourceColumns(expanded, source);
            }
            else
            {
                FromSource? matched = null;
                foreach (var source in sources)
                {
                    if (source.Qualifier is { } q && collation.Equals(q, star.Qualifier))
                    {
                        matched = source;
                        break;
                    }
                }
                if (matched is null)
                    throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound($"{star.Qualifier}.*");
                AppendSourceColumns(expanded, matched);
            }

            expressions.RemoveAt(i);
            expressions.InsertRange(i, expanded);
        }

        static void AppendSourceColumns(List<Expression> destination, FromSource source)
        {
            for (var i = 0; i < source.ColumnNames.Length; i++)
            {
                // SELECT * excludes hidden columns (the period columns on a
                // system-versioned temporal table). Probe-confirmed against
                // SQL Server 2025: `select * from <temporal>` returns the
                // non-hidden columns; explicit references continue to bind.
                if (source.Columns[i].IsHidden)
                    continue;
                var col = source.ColumnNames[i];
                destination.Add(source.Qualifier is { } q
                    ? new Reference(q, col)
                    : new Reference(col));
            }
        }
    }

    /// <summary>
    /// Detects the optional <c>FOR SYSTEM_TIME</c> clause between a table
    /// name and any alias in a FROM source. Returns the composed row
    /// enumerator (parent rows + history rows, time-filtered per form) when
    /// present, or null when the clause isn't there.
    /// </summary>
    /// <remarks>
    /// All five forms parse: <c>ALL</c>, <c>AS OF t</c>,
    /// <c>BETWEEN t1 AND t2</c>, <c>FROM t1 TO t2</c>, and
    /// <c>CONTAINED IN (t1, t2)</c>; anything else is Msg 102. Non-temporal
    /// target raises Msg 13544 here (probe-confirmed wording, qualified-name
    /// form approximated — real SQL Server pads temp-table names with their
    /// internal suffix).
    /// </remarks>
    private static TemporalRowSource? ParseOptionalForSystemTime(ParserContext context, HeapTable heapTable)
    {
        // ConsumeOptionalAlias's contract: caller leaves cursor on the last
        // table-name segment. To peek for FOR SYSTEM_TIME without breaking
        // that contract, save a checkpoint and advance; restore on mismatch.
        var checkpoint = context.SaveCheckpoint();
        var nextToken = context.GetNextOptional();
        if (nextToken is not ReservedKeyword { Keyword: Keyword.For })
        {
            context.RestoreCheckpoint(checkpoint);
            return null;
        }
        var systemTimeToken = context.GetNextOptional();
        if (systemTimeToken is not UnquotedString { ContextualKeyword: ContextualKeyword.System_Time })
        {
            context.RestoreCheckpoint(checkpoint);
            return null;
        }
        if (heapTable.SystemVersioning is null)
            throw SimulatedSqlException.ForSystemTimeRequiresVersionedTable(QualifiedNameFor(context, heapTable));
        var historyTable = heapTable.SystemVersioning;
        if (heapTable.PeriodColumns is not { } pc)
            throw SimulatedSqlException.ForSystemTimeRequiresVersionedTable(QualifiedNameFor(context, heapTable));

        context.MoveNextRequired();
        switch (context.Token)
        {
            // ALL: union of current + history rows, with only the
            // zero-duration filter every form applies.
            case ReservedKeyword { Keyword: Keyword.All }:
                context.MoveNextOptional();
                return new TemporalRowSource(heapTable, historyTable, pc, TemporalQueryKind.All, null, null, context.Batch);
            // AS OF t: rows where start <= t < end.
            case ReservedKeyword { Keyword: Keyword.As }:
                context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Of })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                return new TemporalRowSource(heapTable, historyTable, pc, TemporalQueryKind.AsOf, ParseTemporalTimeArgument(context), null, context.Batch);
            // BETWEEN t1 AND t2: rows active at any point in [t1, t2].
            case ReservedKeyword { Keyword: Keyword.Between }:
                context.MoveNextRequired();
                var betweenLower = ParseTemporalTimeArgument(context);
                if (context.Token is not ReservedKeyword { Keyword: Keyword.And })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                return new TemporalRowSource(heapTable, historyTable, pc, TemporalQueryKind.Between, betweenLower, ParseTemporalTimeArgument(context), context.Batch);
            // FROM t1 TO t2: same, with the upper bound exclusive.
            case ReservedKeyword { Keyword: Keyword.From }:
                context.MoveNextRequired();
                var fromLower = ParseTemporalTimeArgument(context);
                if (context.Token is not ReservedKeyword { Keyword: Keyword.To })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                return new TemporalRowSource(heapTable, historyTable, pc, TemporalQueryKind.FromTo, fromLower, ParseTemporalTimeArgument(context), context.Batch);
            // CONTAINED IN (t1, t2): rows whose whole validity period sits
            // inside the range. The parenthesized two-argument form is the
            // only spelling real accepts (bare arguments are Msg 102).
            case UnquotedString { ContextualKeyword: ContextualKeyword.Contained }:
                context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.In })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                if (context.Token is not Operator { Character: '(' })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                var containedLower = ParseTemporalTimeArgument(context);
                if (context.Token is not Operator { Character: ',' })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                var containedUpper = ParseTemporalTimeArgument(context);
                if (context.Token is not Operator { Character: ')' })
                    throw TemporalSyntaxError(context);
                context.MoveNextOptional();
                return new TemporalRowSource(heapTable, historyTable, pc, TemporalQueryKind.ContainedIn, containedLower, containedUpper, context.Batch);
            default:
                throw TemporalSyntaxError(context);
        }
    }

    /// <summary>
    /// Parses one <c>FOR SYSTEM_TIME</c> time argument. Real SQL Server's
    /// grammar admits only a literal or a variable reference in these
    /// positions — a function call, a parenthesized subquery, or a column
    /// reference is Msg 102 (probe-confirmed against SQL Server 2025:
    /// <c>AS OF SYSUTCDATETIME()</c> and <c>BETWEEN p.ValidFrom AND …</c>
    /// both fail at parse). Leaves the cursor on the token after the
    /// argument, which is where each form's separator (<c>AND</c> /
    /// <c>TO</c> / <c>,</c> / <c>)</c>) or the post-clause lookahead sits.
    /// </summary>
    private static Expression ParseTemporalTimeArgument(ParserContext context)
    {
        Expression argument = context.Token switch
        {
            Numeric number => new Value(number.Value, number.IntegerLiteralDigitCount),
            Literal literal => new Value(literal.Value),
            AtPrefixedString atPrefixed => new VariableReference(atPrefixed, context),
            ReservedKeyword { Keyword: Keyword.Null } => new Value(),
            _ => throw TemporalSyntaxError(context),
        };
        context.MoveNextOptional();
        return argument;
    }

    /// <summary>
    /// The rejection a malformed <c>FOR SYSTEM_TIME</c> clause raises: real
    /// SQL Server splits by what the offending token is — a reserved keyword
    /// gives Msg 156 (<c>BETWEEN 't' TO 't'</c> → "near the keyword 'TO'"),
    /// anything else Msg 102 (<c>FOR SYSTEM_TIME GARBAGE</c>).
    /// </summary>
    private static SimulatedSqlException TemporalSyntaxError(ParserContext context)
        => context.Token is ReservedKeyword reserved
            ? SimulatedSqlException.SyntaxErrorNearKeyword(reserved)
            : SimulatedSqlException.SyntaxErrorNear(context);

    /// <summary>
    /// Builds the <c>database.schema.table</c> qualified name a Msg 13544 /
    /// 13599 rejection message wants. Temp tables aren't tracked under
    /// <c>Database.Schemas</c>, so the schema lookup falls back to
    /// <c>dbo</c> with the host database name <c>tempdb</c> — real SQL
    /// Server pads temp-table names with their internal allocation suffix
    /// (<c>#X____...___…000000000148</c>) which the simulator doesn't carry.
    /// </summary>
    private static string QualifiedNameFor(ParserContext context, HeapTable heapTable)
    {
        if (heapTable.Name.StartsWith('#'))
            return $"tempdb.dbo.{heapTable.Name}";
        var db = context.Batch.CurrentDatabase;
        var schemaName = db.Schemas.Values.FirstOrDefault(s => s.SchemaId == heapTable.SchemaId)?.Name ?? Database.DefaultSchemaName;
        return $"{db.Name}.{schemaName}.{heapTable.Name}";
    }
}

/// <summary>
/// Which <c>FOR SYSTEM_TIME</c> form a <see cref="TemporalRowSource"/>
/// filters by. Each carries the row-version predicate real SQL Server
/// applies over the union of the parent and history rows.
/// </summary>
internal enum TemporalQueryKind
{
    /// <summary>Every row version, no time bound.</summary>
    All,
    /// <summary>The version current at one instant: start &lt;= t &lt; end.</summary>
    AsOf,
    /// <summary>Active anywhere in [t1, t2]: start &lt;= t2 and end &gt; t1.</summary>
    Between,
    /// <summary>Active anywhere in [t1, t2): start &lt; t2 and end &gt; t1.</summary>
    FromTo,
    /// <summary>Whole validity period inside [t1, t2]: start &gt;= t1 and end &lt;= t2.</summary>
    ContainedIn,
}

/// <summary>
/// Lazy row source for a <c>FOR SYSTEM_TIME</c> clause: yields the rows of
/// the parent and its history sibling that satisfy the form's period
/// predicate. Bound expressions are evaluated once on iteration start (no
/// per-row re-evaluation), matching the "constant per query" contract real
/// SQL Server applies to the time arguments.
/// </summary>
/// <remarks>
/// Every form — <c>ALL</c> included — drops rows whose validity period has
/// zero duration (<c>ROW START = ROW END</c>), which is what real SQL Server
/// does: a row updated more than once inside one transaction leaves such a
/// history row behind, and it is physically stored (a direct
/// <c>SELECT</c> against the history table returns it) but invisible to
/// every <c>FOR SYSTEM_TIME</c> form. Probe-confirmed against SQL Server
/// 2025.
/// </remarks>
internal sealed class TemporalRowSource(
    HeapTable parent,
    HeapTable history,
    (int StartOrdinal, int EndOrdinal) period,
    TemporalQueryKind kind,
    Expression? lowerBound,
    Expression? upperBound,
    BatchContext batch) : IEnumerable<byte[]>
{
    public IEnumerator<byte[]> GetEnumerator()
    {
        // Evaluate the bounds once at iteration start. A NULL bound makes
        // every comparison unknown, so the whole source is empty (real
        // returns no rows rather than raising).
        var lower = TemporalRowSource.EvaluateBound(lowerBound, batch);
        var upper = TemporalRowSource.EvaluateBound(upperBound, batch);
        if ((lowerBound is not null && lower is null) || (upperBound is not null && upper is null))
            yield break;

        var startStored = parent.StorageOrdinals[period.StartOrdinal];
        var endStored = parent.StorageOrdinals[period.EndOrdinal];
        var lowerTime = lower ?? default;
        var upperTime = upper ?? default;

        foreach (var bytes in parent.Heap.EnumerateRows())
        {
            if (this.RowMatches(parent.StoredColumns, bytes, parent.Heap, startStored, endStored, lowerTime, upperTime, DateTime.MinValue))
                yield return bytes;
        }
        // A finite HISTORY_RETENTION_PERIOD hides history rows whose validity
        // ended before the window opens. Real applies the same cutoff at query
        // time (its background cleanup task deletes them later), so an aged-out
        // version disappears from every FOR SYSTEM_TIME form the moment the
        // retention period is set.
        var cutoff = parent.HistoryRetentionCutoff(batch.CurrentStatement.UtcNow) ?? DateTime.MinValue;
        foreach (var bytes in history.Heap.EnumerateRows())
        {
            if (this.RowMatches(history.StoredColumns, bytes, history.Heap, startStored, endStored, lowerTime, upperTime, cutoff))
                yield return bytes;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <summary>
    /// Evaluates one bound to a <c>datetime2(7)</c> point, or null when the
    /// expression is absent or evaluates to NULL. The argument's type is
    /// gated the way real gates it — as a comparison against the period
    /// columns: strings and the date/time family (except <c>time</c>)
    /// convert, <c>time</c> and binary raise Msg 402, everything else
    /// (integer, decimal, money, float, bit, uniqueidentifier) raises
    /// Msg 206.
    /// </summary>
    private static DateTime? EvaluateBound(Expression? expression, BatchContext batch)
    {
        if (expression is null)
            return null;
        // The restricted argument grammar admits no column reference, so
        // the resolver is a guard rather than a reachable path.
        var raw = expression.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), batch));
        if (raw.IsNull)
            return null;
        var target = SqlType.GetDateTime2(7);
        if (raw.Type is TimeSqlType or BinarySqlType or VarbinarySqlType)
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(target, raw.Type, "greater than");
        if (raw.Type.Category is not (SqlTypeCategory.String or SqlTypeCategory.DateTime))
            throw SimulatedSqlException.OperandTypeClash(target, raw.Type);
        // EF Core 10 emits the bounds as Varchar / NVarchar literals;
        // coercing to datetime2 lets the period filter compare ticks.
        return raw.CoerceTo(target).AsDateTime2;
    }

    private bool RowMatches(HeapColumn[] storedColumns, byte[] bytes, Heap lobStore, int startStored, int endStored, DateTime lower, DateTime upper, DateTime retentionCutoff)
    {
        var rowStart = RowDecoder.DecodeColumn(storedColumns, bytes, startStored, lobStore).AsDateTime2;
        var rowEnd = RowDecoder.DecodeColumn(storedColumns, bytes, endStored, lobStore).AsDateTime2;
        // Zero-duration versions are invisible to every form, so the
        // period predicate only sees rows that were current for a while.
        return rowStart < rowEnd && rowEnd >= retentionCutoff && kind switch
        {
            TemporalQueryKind.All => true,
            TemporalQueryKind.AsOf => rowStart <= lower && lower < rowEnd,
            TemporalQueryKind.Between => rowStart <= upper && rowEnd > lower,
            TemporalQueryKind.FromTo => rowStart < upper && rowEnd > lower,
            _ => rowStart >= lower && rowEnd <= upper,
        };
    }
}

/// <summary>
/// One entry in an ORDER BY clause: either a positional ordinal (1-based
/// index into the projection) or an arbitrary expression, plus the direction
/// flag.
/// </summary>
internal readonly struct OrderBySpec
{
    public readonly Expression? Expr;
    public readonly int Ordinal;
    public readonly bool Descending;

    public bool IsOrdinal => this.Expr is null;

    private OrderBySpec(Expression? expr, int ordinal, bool descending)
    {
        this.Expr = expr;
        this.Ordinal = ordinal;
        this.Descending = descending;
    }

    public static OrderBySpec FromExpression(Expression expr, bool descending) => new(expr, 0, descending);
    public static OrderBySpec FromOrdinal(int ordinal, bool descending) => new(null, ordinal, descending);
}
