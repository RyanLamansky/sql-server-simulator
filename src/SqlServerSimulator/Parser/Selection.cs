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
    /// The exposed name (alias, else table name) of the single FROM source,
    /// captured for <c>FOR XML AUTO</c>, which names each row element after
    /// its owning table. Null when the query has no single named source
    /// (set-op chains, join shapes); <c>FOR XML AUTO</c> over such a shape
    /// falls back to the multi-source rejection.
    /// </summary>
    internal string? AutoElementName;

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
    /// Per base-table <c>object_id</c>, the 1-based column ordinals this query
    /// reads — the input to the execution-time column-level SELECT check
    /// (Msg 230 / 229). Recorded at parse time (principal-independent, rides the
    /// cached plan) from the resolved column references across the projection,
    /// WHERE, JOIN ON, GROUP BY, HAVING, and ORDER BY of every (sub)query.
    /// A base table present with an <em>empty</em> ordinal set is read without
    /// naming a column (<c>COUNT(*)</c> / <c>SELECT 1</c> / <c>EXISTS</c>), which
    /// real checks as requiring SELECT on every column. Null when the query
    /// reads no base table (constant SELECT / all-system-table). Set once by the
    /// outermost <see cref="ParseQueryExpression"/>.
    /// </summary>
    public Dictionary<int, (Storage.HeapTable Table, HashSet<int> Columns)>? ReadColumnsByObject;

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
    /// Non-null when this Selection is shape-eligible to back an updatable
    /// view: exactly one FROM source, no JOINs, no DISTINCT, no aggregates,
    /// no windows, no GROUP BY, no HAVING, no set-op chain. The
    /// <see cref="ViewUpdatabilityProfile"/> exposes the single source, the
    /// projection expressions, and the WHERE excluders — enough for
    /// <see cref="View"/> to derive its base-column map and re-evaluate
    /// the body's WHERE against a base-table row at DML time. Null for any
    /// other shape; the DML-through-view path inspects the null+
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
    /// The SELECT's ORDER BY items, captured for the updatable-cursor
    /// enumeration path (<see cref="EnumerateForCursor"/>) so KEYSET / DYNAMIC
    /// cursors and positioned DML can order rows the same way a read would.
    /// Non-null only when <see cref="UpdatabilityProfile"/> is set (single
    /// base table, no set-op chain); empty when the cursor's SELECT has no
    /// ORDER BY. Set post-construction by <see cref="BuildSqlProjection"/>.
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
    public static Selection Parse(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver = null) =>
        ParseQueryExpression(context, depth, outerTypeResolver);

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
        }

        var combined = ParseUnionExceptChain(context, depth, outerTypeResolver);

        // Top-level ORDER BY: applies to the combined result (post-set-op).
        // ORDER BY references within set-op chains use the first branch's
        // column names. Top-level OFFSET/FETCH (post-chain) attaches here
        // too; FETCH-without-OFFSET on a single SELECT is also caught here
        // when the cursor sits on FETCH after no ORDER BY was consumed.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var orderBy = new List<OrderBySpec>();
            ParseOrderByItems(context, orderBy);
            var topLevelTail = new FromClause();
            ConsumeOffsetFetch(context, topLevelTail);
            combined = ApplyTopLevelOrderBy(combined, orderBy, topLevelTail.OffsetExpression, topLevelTail.FetchExpression);
        }

        // Trailing FOR JSON { PATH | AUTO } [, options]: wraps the combined
        // result in a single-column JSON-string serializer. Sits where FOR XML
        // / FOR BROWSE do (after ORDER BY / OFFSET-FETCH, before OPTION); a
        // non-JSON FOR clause is left in place for the downstream Msg 102.
        combined = ParseOptionalForJson(context, combined);

        // Trailing FOR XML { RAW | AUTO | PATH } [, ELEMENTS …] [, ROOT …]:
        // wraps the result in a single-column xml serializer. Sits in the same
        // slot; a non-XML FOR clause is left in place for the downstream Msg 102.
        combined = ParseOptionalForXml(context, combined);

        // OPTION (hint [, …]) — statement-level hint clause. Parsed as a
        // closed-list per Selection.Hints.cs; MAXRECURSION applies to in-
        // scope recursive CTEs, everything else recognized is discarded
        // (the simulator has nothing to dispatch on a hint against).
        if (context.Token is ReservedKeyword { Keyword: Keyword.Option })
            ParseOptionClause(context);

        if (ownsSecurableSink)
        {
            if (context.SecurableSink is { Count: > 0 } sink)
                combined.ReferencedSecurables = sink;
            if (context.ReadColumnSink is { Count: > 0 } readColumns)
                combined.ReadColumnsByObject = readColumns;
            context.SecurableSink = null;
            context.ReadColumnSink = null;
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
    private static Selection ParseUnionExceptChain(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var left = ParseIntersectChain(context, depth, outerTypeResolver, isFirstBranch: true);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Union or Keyword.Except } op)
        {
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

            var right = ParseIntersectChain(context, depth, outerTypeResolver, isFirstBranch: false);
            RecordSetOperationForIndexedViewShape(context);
            left = CombineSetOps(left, right, kind);
        }
        return left;
    }

    /// <summary>
    /// Notes a UNION / INTERSECT / EXCEPT for the indexed-view battery
    /// (Msg 10116). Recorded at the two chain sites rather than inside
    /// <c>CombineSetOps</c>, which has no parser context.
    /// </summary>
    private static void RecordSetOperationForIndexedViewShape(ParserContext context)
    {
        if (context.IndexedViewShapeCollector is { } shape)
            shape.HasSetOperation = true;
    }

    /// <summary>
    /// Higher-precedence set-op level: parses a chain of INTERSECT
    /// operators left-to-right.
    /// </summary>
    internal static Selection ParseIntersectChain(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool isFirstBranch)
    {
        var left = ParseSetOpBranch(context, depth, outerTypeResolver, allowOrderBy: isFirstBranch);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Intersect })
        {
            context.MoveNextRequired();
            var right = ParseSetOpBranch(context, depth, outerTypeResolver, allowOrderBy: false);
            RecordSetOperationForIndexedViewShape(context);
            left = CombineSetOps(left, right, SetOpKind.Intersect);
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
    private static Selection ParseSetOpBranch(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool allowOrderBy)
    {
        if (context.Token is not Operator { Character: '(' })
            return ParseSingleSelectStatement(context, depth, outerTypeResolver, allowOrderBy);

        context.MoveNextRequired();
        var inner = ParseUnionExceptChain(context, depth, outerTypeResolver);
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
        var savedAggregateCollector = context.AggregateCollector;
        var savedWindowCollector = context.WindowCollector;
        // ParseInner installs this scope's FROM sources as the outer resolver
        // so the select list can bind against them; restoring it here keeps
        // the enclosing scope intact on every exit path, including throws.
        var savedOuterTypeResolver = context.OuterTypeResolver;
        var aggregates = new List<AggregateExpression>();
        var windows = new List<WindowExpression>();
        var savedEnclosingAggregateCollector = context.EnclosingAggregateCollector;
        context.EnclosingAggregateCollector = savedAggregateCollector;
        context.AggregateCollector = aggregates;
        context.WindowCollector = windows;
        try
        {
            return ParseInner(context, depth, aggregates, windows, outerTypeResolver, allowOrderBy);
        }
        finally
        {
            context.AggregateCollector = savedAggregateCollector;
            context.WindowCollector = savedWindowCollector;
            context.EnclosingAggregateCollector = savedEnclosingAggregateCollector;
            context.OuterTypeResolver = savedOuterTypeResolver;
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
        var count = !resolved.IsNull && resolved.Type == SqlType.Int32
            ? resolved.AsInt32
            : throw SimulatedSqlException.TopFetchRequiresInteger();
        return kind switch
        {
            RowLimitKind.Offset when count < 0 => throw SimulatedSqlException.OffsetMustNotBeNegative(),
            RowLimitKind.Fetch when count < 1 => throw SimulatedSqlException.FetchMustBeGreaterThanZero(),
            _ => count,
        };
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
        var count = resolved.IsNull || !SqlType.IsIntegerCategory(resolved.Type)
            ? throw SimulatedSqlException.TopFetchRequiresInteger()
            : resolved.CoerceTo(SqlType.BigInt).AsInt64;
        return count < 0
            ? throw SimulatedSqlException.TopRowCountMustNotBeNegative()
            : count < candidateCount ? (int)count : candidateCount;
    }

    private static Selection ParseInner(ParserContext context, uint depth, List<AggregateExpression> aggregates, List<WindowExpression> windows, Func<MultiPartName, SqlType>? outerTypeResolver, bool allowOrderBy)
    {
        var distinct = false;
        Expression? topExpression = null;
        var topPercent = false;
        var topWithTies = false;

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
            // swallowing the star and failing near the next token; ParsePrimary
            // stops before any binary operator, leaving `*` for the select list.
            var savedRejectInTop = context.RejectNextValueFor;
            context.RejectNextValueFor = true;
            try
            {
                topExpression = Expression.ParsePrimary(context.MoveNextRequiredReturnSelf());
            }
            finally
            {
                context.RejectNextValueFor = savedRejectInTop;
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
        (int Index, Token? Token) afterSources = default;
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
            }
        }

        do
        {
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
                        ConsumeWhereOrderByWithOuterScope(context, fromClause, [.. sources], outerTypeResolver, allowOrderBy);
                    }
                    else
                    {
                        sources = [];
                        joins = [];
                        ParseFromSourceAndJoins(context, depth, sources, joins, fromClause, outerTypeResolver, allowOrderBy);
                    }

                    if (topExpression is not null && fromClause.OffsetExpression is not null)
                        throw SimulatedSqlException.TopAndOffsetMutuallyExclusive();
                    ExpandStars(context.Batch.CurrentDatabase.Collation, expressions, sources);
                    return BuildSqlProjection(context.Batch, [.. sources], [.. joins], expressions, fromClause, distinct, topExpression, topPercent, topWithTies, aggregates, windows, outerTypeResolver, ResolveAssignmentMode(expressions), intoTarget, context.ReadColumnSink);

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

                case ReservedKeyword { Keyword: Keyword.Where }:
                    ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy);
                    goto ExitWhileTokenLoop;

                case ReservedKeyword { Keyword: Keyword.Order }:
                    if (allowOrderBy)
                        ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy);
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

        if (topExpression is not null && fromClause.OffsetExpression is not null)
            throw SimulatedSqlException.TopAndOffsetMutuallyExclusive();
        if (topWithTies && fromClause.OrderBy.Count == 0)
            throw SimulatedSqlException.TopWithTiesRequiresOrderBy();
        // The FROM-less path bakes its projection values at parse time and
        // never plan-caches (BuildSynthesizedSqlRow disqualifies the batch),
        // so its counts resolve here once, exactly as its projection does.
        // The synthesized shape yields at most one row, so PERCENT collapses to
        // "1 row when pct > 0, else none".
        return BuildSynthesizedSqlRow(context.Batch, expressions, fromClause.Excluders, fromClause.OrderBy,
            topPercent
                ? (topExpression is not null && ResolveTopPercentValue(topExpression, context.Batch) > 0 ? 1 : 0)
                : ResolveRowCountLimit(topExpression, RowLimitKind.Top, context.Batch),
            ResolveRowCountLimit(fromClause.OffsetExpression, RowLimitKind.Offset, context.Batch),
            ResolveRowCountLimit(fromClause.FetchExpression, RowLimitKind.Fetch, context.Batch),
            ResolveAssignmentMode(expressions), intoTarget, context.OuterTypeResolver ?? outerTypeResolver);
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
    private static (int Index, Token? Token)? FindOwnFromClause(ParserContext context)
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
        ConsumeWhereOrderByWithOuterScope(context, fromClause, [.. sources], outerTypeResolver, allowOrderBy);
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
        ParseExplicitJoinChain(context, depth, sources, joins, outerTypeResolver);

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
            ParseExplicitJoinChain(context, depth, sources, joins, outerTypeResolver);
        }
    }

    private static void ParseExplicitJoinChain(
        ParserContext context,
        uint depth,
        List<FromSource> sources,
        List<JoinSpec> joins,
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        // A parenthesized join group as the leftmost item — `(A JOIN B ON …)
        // [LEFT] JOIN C …` — is a pure grammar grouping: a left-deep spine
        // already groups its left operand, so the group's interior sources /
        // joins splice directly into this chain with no group marker.
        if (NextSourceIsJoinGroup(context))
            ParseJoinGroup(context, depth, sources, joins, outerTypeResolver);
        else
            sources.Add(ParseSingleFromSource(context, depth, outerTypeResolver));

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
                ParseJoinGroup(context, depth, sources, joins, outerTypeResolver);
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
                    groupOn = BooleanExpression.Parse(context);
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
            sources.Add(ParseSingleFromSource(context, depth, outerTypeResolver));
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
                var savedRejectInOn = context.RejectNextValueFor;
                context.RejectNextValueFor = true;
                try
                {
                    on = BooleanExpression.Parse(context);
                }
                finally
                {
                    context.RejectNextValueFor = savedRejectInOn;
                }
            }
            joins.Add(new JoinSpec(kind, on));
        }
    }

    /// <summary>
    /// Peeks whether the FROM source about to be parsed is a parenthesized
    /// join group — an opening <c>(</c> whose first interior token is not
    /// <c>SELECT</c> (a derived table) or <c>VALUES</c> (a table-value
    /// constructor). Entered with the cursor on the token preceding the source
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
        return opensParen && interior is not (null or ReservedKeyword { Keyword: Keyword.Select or Keyword.Values });
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
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var joinsBefore = joins.Count;
        context.MoveNextRequired();
        ParseExplicitJoinChain(context, depth, sources, joins, outerTypeResolver);
        if (joins.Count == joinsBefore || context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
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

        // OPENQUERY is a reserved keyword (not a Name), so it can't ride the
        // name-string dispatch below. It never correlates to the left APPLY
        // sources — its arguments are a server identifier and a constant
        // pass-through string — so route it straight back through
        // ParseSingleFromSource.
        if (next is ReservedKeyword { Keyword: Keyword.OpenQuery })
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
                || string.Equals(nextName.Value, "GENERATE_SERIES", StringComparison.OrdinalIgnoreCase))
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
                    return ParseXmlNodesSource(context);
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

        var lateralPlan = Selection.Parse(context, depth + 1, outerTypeResolver: ChainedResolver);

        var schema = lateralPlan.Schema;
        var columnNames = lateralPlan.ColumnNames;
        var lateralColumns = new HeapColumn[schema.Length];
        for (var ci = 0; ci < lateralColumns.Length; ci++)
            lateralColumns[ci] = new HeapColumn(string.Empty, schema[ci], maxLength: null, nullable: true);

        var alias = ConsumeOptionalAlias(context);

        return new FromSource(
            qualifier: alias,
            columnNames: columnNames,
            columns: lateralColumns,
            storedSchema: lateralColumns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: lateralPlan);
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
    /// the execution-time SELECT permission check. Skips temp tables
    /// (<c>#foo</c>) — those aren't permission-checked — and no-ops when no sink
    /// is active (a module body, or a context that isn't tracking reads).
    /// </summary>
    private static void RecordSecurableRead(ParserContext context, Schemas.SchemaObject obj, MultiPartName name)
    {
        if (context.SecurableSink is not { } sink || name.Leaf.StartsWith('#'))
            return;
        sink.Add(new ReferencedSecurable(obj.ObjectId, obj.SchemaId, obj.Name, name.ImmediateQualifier ?? Database.DefaultSchemaName));
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
                        lateralPlan: cteBinding.Plan);
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
                    _ = ParseOptionalTableHints(context);
                    RecordSecurableRead(context, resolvedView, objectName);
                    return new FromSource(
                        qualifier: viewAlias ?? resolvedView.Name,
                        columnNames: viewColumnNames,
                        columns: resolvedView.OutputColumns,
                        storedSchema: resolvedView.OutputColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: Selection.ForView(resolvedView),
                        backingView: resolvedView);
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
                        RecordSecurableRead(context, function, objectName);
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
                    throw SimulatedSqlException.InvalidObjectName(objectName);
                }

                var heapColumnNames = new string[heapTable.Columns.Length];
                for (var ci = 0; ci < heapColumnNames.Length; ci++)
                    heapColumnNames[ci] = heapTable.Columns[ci].Name;

                // Optional FOR SYSTEM_TIME (ALL | AS OF expr) between the
                // table name and any alias. Only legal on a system-versioned
                // parent; rejected on non-temporal tables (probe-confirmed
                // Msg 13510 wording — surfaced via a SyntaxErrorNear here
                // since the simulator doesn't carry the exact message).
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
                var heapHints = ParseOptionalTableHints(context);
                ValidateIndexHintArguments(context.Batch.CurrentDatabase.Collation, heapHints, heapTable, $"{objectName.ImmediateQualifier ?? Database.DefaultSchemaName}.{heapTable.Name}");
                // Phase 1b: acquire table-level IS/IX/S/X (based on hints +
                // isolation level) and capture the per-row plan. Temporal
                // FOR SYSTEM_TIME sources bypass the per-row probe (they
                // materialize through a separate path that doesn't expose
                // RIDs).
                var heapPlan = context.Batch.AcquireDataLockIfApplicable(heapTable, heapHints, isWrite: false);
                RecordSecurableRead(context, heapTable, objectName);
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
                    heapPlan: temporalRowSource is null ? heapPlan : null);

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
                    backingTable: tvTable);

            case Operator { Character: '(' }:
                var afterOpenParen = context.GetNextRequired();

                // Table-value-constructor derived table: `(VALUES …) alias(cols)`.
                // Rides the same deferred lateral-plan seam as a derived-table
                // SELECT, so a VALUES source correlates to outer scope the same
                // way (needed for a comma-FROM VALUES referencing an outer CTE).
                if (afterOpenParen is ReservedKeyword { Keyword: Keyword.Values })
                    return ParseValuesDerivedTable(context, context.OuterTypeResolver ?? outerTypeResolver);

                if (afterOpenParen is not ReservedKeyword { Keyword: Keyword.Select })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

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
                var derivedSelection = Selection.Parse(context, depth + 1,
                    outerTypeResolver: context.OuterTypeResolver ?? outerTypeResolver);

                // Inner SELECT result rows are LOB-inline (projections never
                // emit LOB pointers because they have no destination Heap),
                // so build a HeapColumn[] schema from the SqlType[] so the
                // decoder still strips marker bytes for text/ntext/image
                // columns; lobStore is null because no chain to follow.
                var derivedColumns = new HeapColumn[derivedSelection.Schema.Length];
                for (var ci = 0; ci < derivedColumns.Length; ci++)
                    derivedColumns[ci] = new HeapColumn(string.Empty, derivedSelection.Schema[ci], maxLength: null, nullable: true);

                // Derived tables have no native name; the alias is the
                // qualifier when present, otherwise null disables the
                // qualified-reference check (the existing simulator
                // accepts derived tables without alias, unlike real SQL).
                var derivedQualifier = ConsumeOptionalAlias(context);

                return new FromSource(
                    qualifier: derivedQualifier,
                    columnNames: derivedSelection.ColumnNames,
                    columns: derivedColumns,
                    storedSchema: derivedColumns,
                    storageOrdinals: null,
                    lobStore: null,
                    rows: [],
                    lateralPlan: derivedSelection);

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

            case ReservedKeyword
            {
                Keyword: Keyword.ContainsTable or Keyword.FreeTextTable
                    or Keyword.SemanticKeyPhraseTable or Keyword.SemanticSimilarityTable
                    or Keyword.SemanticSimilarityDetailsTable
            } ftRowset:
                throw new NotSupportedException(
                    $"Full-text rowset functions ({ftRowset.Keyword.ToString().ToUpperInvariant()}) are not modeled.");

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
        var columns = new HeapColumn[arity];
        for (var c = 0; c < arity; c++)
        {
            var nullable = false;
            for (var i = 0; i < tuples.Count && !nullable; i++)
                nullable = tuples[i][c].ResultIsNullable(static _ => true);
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
                kind = JoinKind.Left;
                return true;

            case Keyword.Right:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                kind = JoinKind.Right;
                return true;

            case Keyword.Full:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
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
        bool allowOrderBy)
    {
        SqlType MyResolver(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

        var saved = context.OuterTypeResolver;
        context.OuterTypeResolver = MyResolver;
        try
        {
            ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy);
        }
        finally
        {
            context.OuterTypeResolver = saved;
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
    private static void ConsumeWhereAndOrderBy(ParserContext context, FromClause fromClause, bool allowOrderBy)
    {
        // WHERE / GROUP BY / HAVING reject windowed functions (Msg 4108) and
        // NEXT VALUE FOR (Msg 11720). Toggle the parser-context flags for the
        // duration of those parses; ORDER BY (which DOES allow windows but
        // rejects NEXT VALUE FOR) handled separately below.
        var savedAllowsWindows = context.AllowsWindowExpressions;
        var savedRejectNextValueFor = context.RejectNextValueFor;
        context.AllowsWindowExpressions = false;
        context.RejectNextValueFor = true;
        try
        {
            while (context.Token is ReservedKeyword { Keyword: Keyword.Where })
            {
                fromClause.Excluders.Add(BooleanExpression.Parse(context.MoveNextRequiredReturnSelf()));
            }

            if (context.Token is ReservedKeyword { Keyword: Keyword.Group })
            {
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                ParseGroupByList(context, fromClause);
            }

            if (context.Token is ReservedKeyword { Keyword: Keyword.Having })
            {
                fromClause.Having = BooleanExpression.Parse(context.MoveNextRequiredReturnSelf());
            }
        }
        finally
        {
            context.AllowsWindowExpressions = savedAllowsWindows;
            context.RejectNextValueFor = savedRejectNextValueFor;
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
            context.RejectNextValueFor = true;
            try
            {
                ParseOrderByItems(context, fromClause.OrderBy);
            }
            finally
            {
                context.RejectNextValueFor = savedRejectNextValueFor;
            }
            ConsumeOffsetFetch(context, fromClause);
        }

        // All `OVER w` references (projection and ORDER BY) and the WINDOW
        // definitions are now parsed — bind each pending reference.
        ResolvePendingNamedWindows(context);
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
            var body = Expressions.WindowExpression.ParseWindowBody(context);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.NamedWindowDefinitions[nameToken.Value] = body;
            context.MoveNextOptional();
        } while (context.Token is Operator { Character: ',' });
    }

    /// <summary>
    /// Binds each pending bare <c>OVER w</c> reference to its named-window
    /// definition, then clears the query-block's pending / definition state.
    /// An unresolved name raises Msg 5362 ("Window 'w' is undefined.").
    /// </summary>
    private static void ResolvePendingNamedWindows(ParserContext context)
    {
        if (context.PendingNamedWindows.Count == 0)
            return;
        foreach (var (window, name) in context.PendingNamedWindows)
        {
            if (!context.NamedWindowDefinitions.TryGetValue(name, out var body))
                throw SimulatedSqlException.WindowIsUndefined(name);
            window.ApplyNamedWindow(body);
        }
        context.PendingNamedWindows.Clear();
        context.NamedWindowDefinitions.Clear();
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

            if (context.AggregatesParsed > aggregatesBefore || context.SubqueriesParsed > subqueriesBefore)
                throw SimulatedSqlException.AggregateOrSubqueryInGroupBy();

            // The empty grouping set contributes no expression at all, so there
            // is nothing for Msg 164 to require a column of. Probe-confirmed
            // legal on real 2026-07-24: `GROUP BY ()`, `GROUPING SETS (())`,
            // `GROUPING SETS ((a),())` and `GROUP BY (), a` all return rows.
            var contributesAnExpression = false;
            foreach (var fragment in contribution)
                contributesAnExpression |= fragment.Length > 0;

            if (contributesAnExpression && context.ColumnReferencesParsed == columnsBefore)
                throw SimulatedSqlException.GroupByExpressionHasNoLocalColumn();
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
    /// keyword (default ASC). A pure positive-integer literal is recorded as
    /// an ordinal reference into the projection rather than a constant
    /// expression; constant non-integer expressions silently sort by their
    /// constant (SQL Server's Msg 408 rejection isn't modeled).
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

            // A bare integer literal is the ordinal form (validated against the
            // projection count later in BuildSqlProjection). Anything else —
            // including a constant arithmetic expression like `1+0` — falls
            // through to per-row evaluation; SQL Server's Msg 408 rejection of
            // constant ORDER BY expressions isn't modeled.
            if (expr is Value valExpr
                && valExpr.Constant.Type == SqlType.Int32
                && !valExpr.Constant.IsNull)
            {
                orderBy.Add(OrderBySpec.FromOrdinal(valExpr.Constant.AsInt32, descending));
            }
            else
            {
                orderBy.Add(OrderBySpec.FromExpression(expr, descending));
            }
        }
        while (context.Token is Operator { Character: ',' });
    }

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
    private static Selection BuildSynthesizedSqlRow(BatchContext parseBatch, List<Expression> expressions, List<BooleanExpression> excluders, List<OrderBySpec> orderBy, int? topCount, int? offsetCount, int? fetchCount, bool isAssignmentOnly, MultiPartName? intoTarget, Func<MultiPartName, SqlType>? outerTypeResolver)
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
        var referencesOuterColumns = false;
        foreach (var expression in expressions)
            expression.VisitColumnReferences(_ => referencesOuterColumns = true);

        SqlType TypeResolver(MultiPartName column) =>
            outerTypeResolver is not null
                ? outerTypeResolver(column)
                : throw SimulatedSqlException.InvalidColumnName(column);

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
            ? ComputeIntoDestSchema(target, expressions, schema, columnNames, [], [])
            : null)
        {
            ProjectionExpressions = [.. expressions],
            ColumnIntegerLiteralDigits = LiteralDigitsOf(expressions),
            ColumnReportsNumeric = ColumnReportsNumericOf(expressions, schema),
            // A FROM-less projection has no sources, so column nullability is
            // the per-expression rule alone (literals NOT NULL, other
            // expressions nullable) — matching real's result metadata
            // (`select 1` → Int, not IntN). The resolver is never consulted
            // (no column can appear without a source).
            ColumnNullability = ComputeColumnNullability(expressions, [], []),
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
    /// Detects the optional <c>FOR SYSTEM_TIME ALL | AS OF expr</c> clause
    /// between a table name and any alias in a FROM source. Returns the
    /// composed row enumerator (parent rows + history rows, optionally
    /// time-filtered) when present, or null when the clause isn't there.
    /// </summary>
    /// <remarks>
    /// Only <c>ALL</c> and <c>AS OF</c> ship; <c>FROM … TO …</c>,
    /// <c>BETWEEN … AND …</c>, and <c>CONTAINED IN (…, …)</c> raise
    /// <see cref="NotSupportedException"/> until an application emission
    /// requires them. Non-temporal target raises Msg 13544 here
    /// (probe-confirmed wording, qualified-name form approximated — real
    /// SQL Server pads temp-table names with their internal suffix).
    /// </remarks>
    private static IEnumerable<byte[]>? ParseOptionalForSystemTime(ParserContext context, HeapTable heapTable)
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
            // ALL: union of current + history rows, no time filter.
            case ReservedKeyword { Keyword: Keyword.All }:
                context.MoveNextOptional();
                return heapTable.Rows.Concat(historyTable.Rows);
            // AS OF expr: parent + history rows where start <= expr < end.
            case ReservedKeyword { Keyword: Keyword.As }:
                context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Of })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                var timeExpr = Expression.Parse(context);
                return new TemporalAsOfRowSource(heapTable, historyTable, pc, timeExpr, context.Batch);
            default:
                throw new NotSupportedException("Only FOR SYSTEM_TIME ALL and FOR SYSTEM_TIME AS OF <expr> are modeled. BETWEEN / FROM … TO / CONTAINED IN are deferred.");
        }
    }

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
/// Lazy row source for <c>FOR SYSTEM_TIME AS OF expr</c>: yields rows from
/// the parent and the history sibling whose period brackets the evaluated
/// time point (start &lt;= t &lt; end). The time expression is evaluated
/// once on iteration start (no per-row re-evaluation), matching the
/// "constant per query" contract real SQL Server applies to the AS OF
/// time expression.
/// </summary>
internal sealed class TemporalAsOfRowSource(HeapTable parent, HeapTable history, (int StartOrdinal, int EndOrdinal) period, Expression timeExpr, BatchContext batch) : IEnumerable<byte[]>
{
    public IEnumerator<byte[]> GetEnumerator()
    {
        // Evaluate the time expression once at iteration start. The
        // resolver throws on any column reference — AS OF's expression
        // is a parse-time / batch-state constant in real SQL Server.
        var raw = timeExpr.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), batch));
        // EF Core 10 emits AS OF '<iso-literal>' as a Varchar / NVarchar
        // literal; coerce to datetime2 so the period filter compares ticks.
        var timePoint = raw.CoerceTo(SqlType.GetDateTime2(7)).AsDateTime2;
        var startStored = parent.StorageOrdinals[period.StartOrdinal];
        var endStored = parent.StorageOrdinals[period.EndOrdinal];

        foreach (var bytes in parent.Heap.EnumerateRows())
        {
            if (TemporalAsOfRowSource.RowMatches(parent.StoredColumns, bytes, parent.Heap, startStored, endStored, timePoint))
                yield return bytes;
        }
        foreach (var bytes in history.Heap.EnumerateRows())
        {
            if (TemporalAsOfRowSource.RowMatches(history.StoredColumns, bytes, history.Heap, startStored, endStored, timePoint))
                yield return bytes;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    private static bool RowMatches(HeapColumn[] storedColumns, byte[] bytes, Heap lobStore, int startStored, int endStored, DateTime timePoint)
    {
        var rowStart = RowDecoder.DecodeColumn(storedColumns, bytes, startStored, lobStore).AsDateTime2;
        var rowEnd = RowDecoder.DecodeColumn(storedColumns, bytes, endStored, lobStore).AsDateTime2;
        return rowStart <= timePoint && timePoint < rowEnd;
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
