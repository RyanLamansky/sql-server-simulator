using System.Runtime.ExceptionServices;
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
/// (Parse / ParseInner / WHERE / GROUP BY / HAVING / ORDER BY /
/// tableless-SELECT shortcut); FROM-source + JOIN parsing lives in
/// <c>Selection.FromClause.cs</c>. The execution-side
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
    /// The <c>ON</c> clause after a <c>SELECT … INTO</c> target, placing the
    /// new table, or null without one.
    /// </summary>
    public DataSpaceClause? IntoDataSpace;

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
    /// Whether this plan's own query block reads a <see cref="RowLocator"/>
    /// (or a nested block correlated to it does), so its execution installs
    /// <see cref="StatementContext.RowAddresses"/> before its sources produce a
    /// row. Set post-construction by <see cref="BuildSqlProjection"/>.
    /// </summary>
    internal bool InstallsRowAddresses;

    /// <summary>
    /// Whether this plan reads one FROM source through a plain projection, so
    /// a <see cref="ExecuteWithRowAddresses"/> run can ask it for the address
    /// of that source's row behind each row it yields — a write passes through
    /// such a body. Set post-construction by <see cref="BuildSqlProjection"/>
    /// and by a pushed copy of one.
    /// </summary>
    internal bool CarriesRowAddresses;

    /// <summary>
    /// Set on a parenthesized set-operation branch whose <c>ORDER BY</c>
    /// chooses the rows its <c>TOP</c> or <c>OFFSET</c> takes, which a nested
    /// query's branch may carry where a bare one may not.
    /// </summary>
    internal bool OrdersOwnRows;

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
    /// Per column, the TDS COLMETADATA flags beyond nullability — see
    /// <see cref="SimulatedQueryResult.ColumnWireFlags"/>; null for a shape
    /// that sets none (every column then reads as a plain updatable one).
    /// </summary>
    internal byte[]? ColumnWireFlags;

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
    /// Per-column flag for projection columns that are the bare untyped
    /// <c>NULL</c>; null when no column is. Set-op unification lets such a
    /// column yield to its partner branch's type instead of forcing the
    /// placeholder <c>int</c> (<c>SELECT 'a' UNION ALL SELECT NULL</c> is
    /// <c>varchar</c>), and the flag survives a combine only where both
    /// branches carried it. Set post-construction alongside
    /// <see cref="ColumnIntegerLiteralDigits"/>.
    /// </summary>
    internal bool[]? ColumnIsUntypedNull;

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

    /// <summary>
    /// Per-column user alias type a projection column carries (see
    /// <c>Expression.ResultAliasType</c>); null when no column carries one.
    /// A view, derived table, CTE or <c>SELECT … INTO</c> column built from
    /// this query keeps it.
    /// </summary>
    internal Schemas.AliasType?[]? ColumnAliasTypes;

    /// <summary>
    /// Per output column, the identity it passes straight through: a direct
    /// reference (aliases aside) to a column that is one, over
    /// a single source apart from APPLY — through a derived table, <c>TOP</c>,
    /// <c>GROUP BY</c>, <c>DISTINCT</c> or another view, but not an expression,
    /// a join or a set operation (probed 2026-09-26 against SQL Server 2025).
    /// A view's column reports it in <c>sys.columns.is_identity</c>. Null when
    /// no column does.
    /// </summary>
    internal IdentityState?[]? ColumnIdentitySources;

    /// <summary>
    /// Per output column, how Dynamic Data Masking masks it for a principal
    /// without <c>UNMASK</c> (see <see cref="DataMask"/>); null when no column
    /// reads a masked one, which is every query while no mask exists. Settled
    /// at compile, so it rides the cached plan; whether it applies is asked of
    /// the executing principal at the statement's output.
    /// </summary>
    internal DataMask?[]? ColumnMasks;

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
    /// streams the bytes — or, for a view <see cref="CatalogRowCache"/> can
    /// serve, hands back its cached rows. Either way a change made earlier in
    /// the same batch (CREATE TABLE, CREATE SCHEMA, DROP TABLE) appears in the
    /// next read, since each such change invalidates the cache.
    /// </summary>
    internal static Selection ForCatalogView(CatalogView view, Database targetDatabase)
    {
        var (schema, columnNames) = CatalogViewShape(view);
        return new Selection(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (batch, _) => ScanCatalogView(view, targetDatabase, batch, checkedRead: false));
    }

    /// <summary>
    /// Every row of <paramref name="view"/> over <paramref name="targetDatabase"/>:
    /// this statement's earlier drained read of it, else the cached rowset, else
    /// a fresh generation (remembered for the rest of the statement once fully
    /// drained). <paramref name="checkedRead"/> says the caller already ran the
    /// view's read-permission check.
    /// </summary>
    private static IEnumerable<byte[]> ScanCatalogView(CatalogView view, Database targetDatabase, BatchContext batch, bool checkedRead)
    {
        var statement = batch.CurrentStatement;
        var key = (view, targetDatabase);
        if (statement.CatalogViewRows is { } memo && memo.TryGetValue(key, out var cached))
        {
            CatalogPushdownDiagnostics.Sink?.Add($"CachedScan({view.Name})");
            return cached;
        }
        if (!checkedRead)
        {
            PermissionEnforcement.CheckCatalogViewRead(batch, view, targetDatabase);
            if (CachedCatalogRows(view, batch, targetDatabase) is { } set)
                return set.Rows;
        }
        CatalogPushdownDiagnostics.Sink?.Add($"Scan({view.Name})");
        var gated = BuiltInResources.ApplyDmvGate(view, batch, view.RowGenerator(batch, targetDatabase));
        var rows = BuiltInResources.ApplyMetadataFilter(view, batch, targetDatabase, gated);
        var encoded = rows.Select(values => RowEncoder.EncodeRow(view.Columns, view.Conform(values)));
        return view.StableWithinStatement ? RememberWhenFullyDrained(statement, key, encoded) : encoded;
    }

    /// <summary>
    /// The <see cref="CatalogRowCache"/> rowset serving a read of
    /// <paramref name="view"/> over <paramref name="targetDatabase"/>, or null
    /// when this read has to generate: the view isn't
    /// <see cref="CatalogView.Cacheable"/>, the target is <c>tempdb</c> (whose
    /// listing includes the reader's own <c>#temp</c> tables) or a database this
    /// simulation doesn't hold, or the session's metadata visibility filters the
    /// view. The caller has already run the view's read-permission check.
    /// </summary>
    internal static CatalogRowSet? CachedCatalogRows(CatalogView view, BatchContext batch, Database targetDatabase)
    {
        if (!view.Cacheable || targetDatabase.Name == Simulation.TempdbDatabaseName)
            return null;
        var simulation = batch.Connection.Simulation;
        return simulation.Databases.TryGetValue(targetDatabase.Name, out var held) && ReferenceEquals(held, targetDatabase)
            && BuiltInResources.ReadsUnfiltered(view, batch, targetDatabase)
            ? simulation.CatalogRows.GetOrBuild(view, targetDatabase, batch, GenerateCatalogRows)
            : null;
    }

    private static List<byte[]> GenerateCatalogRows(CatalogView view, BatchContext batch, Database database)
    {
        var rows = new List<byte[]>();
        foreach (var values in view.RowGenerator(batch, database))
            rows.Add(RowEncoder.EncodeRow(view.Columns, view.Conform(values)));
        return rows;
    }

    /// <summary>
    /// Streams a catalog view's rows through unchanged, remembering them on
    /// <see cref="StatementContext.CatalogViewRows"/> only once the consumer
    /// has drained the whole sequence. That condition is the point: a read
    /// that stops early (a <c>TOP 1</c>, an <c>EXISTS</c>) keeps streaming and
    /// pays nothing to materialize, while a read that went to the end has
    /// already built every row and can hand the next execution the finished
    /// array. Abandoning the enumerator simply skips the store — the loop
    /// never reaches its end — so a partial pass can't be mistaken for a
    /// complete one.
    /// </summary>
    private static IEnumerable<byte[]> RememberWhenFullyDrained(
        StatementContext statement, (CatalogView View, Database Database) key, IEnumerable<byte[]> rows)
    {
        List<byte[]> captured = [];
        foreach (var row in rows)
        {
            captured.Add(row);
            yield return row;
        }
        (statement.CatalogViewRows ??= [])[key] = [.. captured];
    }

    /// <summary>
    /// Predicate-pushdown variant of <see cref="ForCatalogView(CatalogView,Database)"/>:
    /// the WHERE equality <c>&lt;pushdownColumn&gt; = &lt;comparand&gt;</c> — or
    /// the equality family of an <c>IN</c> list, one comparand per member — is
    /// evaluated once per execution (each comparand holds one value for the
    /// execution, so a column resolver is never consulted for this source) and
    /// seeks the view's cached rows (<see cref="CatalogRowSet.Seek"/>), or, where
    /// the read can't be served from the cache, narrows the view's
    /// <see cref="CatalogView.FilteredRowGenerator"/> on a column it keys. The
    /// enclosing SELECT keeps applying the full WHERE as a residual filter, so
    /// this only narrows the source — never the result. Comparands that are all
    /// NULL yield no rows (<c>= NULL</c> is UNKNOWN for every candidate). The
    /// values are resolved per execution (variables / parameters differ between
    /// runs), keeping the compiled plan shareable across sessions.
    /// </summary>
    internal static Selection ForCatalogView(CatalogView view, Database targetDatabase, string pushdownColumn, Expression[] comparands)
    {
        var (schema, columnNames) = CatalogViewShape(view);
        var ordinal = Array.FindIndex(view.Columns, column => BuiltInToken.Equals(column.Name, pushdownColumn));
        var filteredGenerator = comparands.Length == 1
            && view.PushdownColumns is { } keyed && Array.Exists(keyed, column => BuiltInToken.Equals(column, pushdownColumn))
            ? view.FilteredRowGenerator
            : null;
        return new Selection(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (batch, outerResolver) =>
            {
                // A comparand may name an enclosing query's column — the
                // correlated `WHERE ic.object_id = t.object_id` of a CROSS
                // APPLY or subquery body. That is one value for the whole of
                // this execution, which is exactly what the seek needs, and it
                // is how real plans the same query: a correlated catalog read
                // is an index seek carrying OUTER REFERENCES, never a scan.
                var runtime = new RuntimeContext(
                    outerResolver ?? (name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name)), batch);
                var values = new SqlValue[comparands.Length];
                var allNull = true;
                for (var i = 0; i < comparands.Length; i++)
                {
                    values[i] = comparands[i].Run(runtime);
                    allNull &= values[i].IsNull;
                }
                CatalogPushdownDiagnostics.Sink?.Add(
                    allNull ? $"SeekEmpty({view.Name}.{pushdownColumn})" : $"Seek({view.Name}.{pushdownColumn})");
                PermissionEnforcement.CheckCatalogViewRead(batch, view, targetDatabase);
                if (CachedCatalogRows(view, batch, targetDatabase) is { } set)
                    return set.Seek(ordinal, values);
                // Past the cache, a column the view's own filtered generator
                // keys narrows that; any other seek reads the whole view, which
                // the residual predicate then filters.
                if (filteredGenerator is null && !allNull)
                    return ScanCatalogView(view, targetDatabase, batch, checkedRead: true);
                var generated = filteredGenerator is not null
                    ? filteredGenerator(batch, targetDatabase, new CatalogFilter(pushdownColumn, values[0]))
                    : [];
                var gated = BuiltInResources.ApplyDmvGate(view, batch, generated);
                var rows = BuiltInResources.ApplyMetadataFilter(view, batch, targetDatabase, gated);
                return rows.Select(row => RowEncoder.EncodeRow(view.Columns, view.Conform(row)));
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
            rowSource: (batch, outerResolver) => EnumerateValuesRows(schema, tuples, batch, outerResolver))
        {
            // One row merges into its reader like a FROM-less SELECT; a longer
            // list is a constant scan whose values are drawn once each (probed
            // 2026-09-28 against SQL Server 2025).
            VolatileColumns = VolatileProjection.Of([.. tuples.SelectMany(tuple => tuple)], fixesValues: tuples.Count > 1),
            IsSingleConstantRow = tuples.Count == 1,
        };

    /// <summary>
    /// A <c>VALUES</c> list's rows. One row merges into its reader, so its
    /// values are computed as it is read, past an <c>INSERT</c>'s identity
    /// draw; a longer list is a constant scan, which computes every row before
    /// the first goes out — so a later row's error comes ahead of the first row
    /// (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static byte[][] EnumerateValuesRows(SqlType[] schema, List<Expression[]> tuples, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        SqlValue Resolve(MultiPartName name) =>
            outerResolver is not null ? outerResolver(name) : throw SimulatedSqlException.InvalidColumnName(name);
        var runtime = new RuntimeContext(Resolve, batch);
        var rows = new byte[tuples.Count][];
        for (var r = 0; r < tuples.Count; r++)
        {
            var tuple = tuples[r];
            var values = new SqlValue[schema.Length];
            try
            {
                for (var c = 0; c < schema.Length; c++)
                {
                    var raw = tuple[c].Run(runtime);
                    values[c] = raw.IsNull || raw.Type == schema[c] ? raw : raw.CoerceTo(schema[c]);
                }
            }
            catch (SimulatedSqlException valueError)
            {
                valueError.RaisedInRowProjection = tuples.Count == 1;
                throw;
            }
            rows[r] = RowEncoder.EncodeRow(schema, values);
        }
        return rows;
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
        batch.RowAddressProbe is { } probe && !ReferenceEquals(probe, this) && this.CarriesRowAddresses
            && batch.CurrentStatement.RowAddresses is { } addresses
            ? this.ExecuteCarryingRowAddresses(batch, outerResolver, addresses)
            : this.ExecuteRows(batch, outerResolver);

    private SimulatedSqlResultSet ExecuteRows(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver) =>
        this.valueRowSource is { } values
            ? new SimulatedSqlResultSet(this.Schema, this.ColumnNames, values(batch, outerResolver)) { ColumnNullability = this.ColumnNullability, ColumnReportsNumeric = this.ColumnReportsNumeric, ColumnAliasTypes = this.ColumnAliasTypes, ColumnIdentitySources = this.ColumnIdentitySources, ColumnWireFlags = this.ColumnWireFlags, HiddenColumnCount = this.HiddenColumnCount, Browse = this.Browse }
            : new SimulatedSqlResultSet(this.Schema, this.ColumnNames, this.rowSource!(batch, outerResolver)) { ColumnNullability = this.ColumnNullability, ColumnReportsNumeric = this.ColumnReportsNumeric, ColumnAliasTypes = this.ColumnAliasTypes, ColumnIdentitySources = this.ColumnIdentitySources, ColumnWireFlags = this.ColumnWireFlags, HiddenColumnCount = this.HiddenColumnCount, Browse = this.Browse };

    /// <summary>
    /// Runs this plan with the heap address of its first FROM source's row
    /// appended to every row it yields, as a <c>bigint</c>
    /// <see cref="RowLocator.Unpack"/> reads (NULL when that source's row came
    /// from no heap). The address is projected alongside the body's own
    /// columns, so it passes through a <c>TOP</c>, a sort or a window function
    /// with the row it belongs to — what a write through a row-limited or
    /// windowed body needs to find the base row behind each row it yields. A
    /// plan built by another path (a set operation, a constant row) yields its
    /// rows unchanged, one value short.
    /// </summary>
    internal List<SqlValue[]> ExecuteWithRowAddresses(BatchContext batch)
    {
        using var probe = ParserScope.Enter(ref batch.RowAddressProbe, this);
        batch.CurrentStatement.RowAddresses ??= new();
        return [.. this.Execute(batch).RowValues];
    }

    /// <summary>
    /// The <see cref="BatchContext.RowAddressProbe"/> a joined write installs
    /// while it walks an <c>APPLY</c>'s correlated body as its target: no plan
    /// is it, so the body — and every single-source plan the walk runs —
    /// carries its rows' base addresses into the statement's map.
    /// </summary>
    internal static readonly Selection AddressCarryingWalk = ForValuesConstructor([], [], []);

    /// <summary>
    /// Runs this plan as a source a <see cref="ExecuteWithRowAddresses"/> run
    /// reads — a CTE or derived table whose body a write can pass through —
    /// carrying the address of its own first source's row the way the run's
    /// plan does, and recording each row it yields against that address, so
    /// the run's locator reads through the re-encoding to the base row. A view
    /// does the same in <c>Simulation.InvokeView</c>.
    /// </summary>
    private SimulatedSqlResultSet ExecuteCarryingRowAddresses(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, RowAddressMap addresses)
    {
        IEnumerable<SqlValue[]> rows;
        using (ParserScope.Enter(ref batch.RowAddressProbe, this))
            rows = this.ExecuteRows(batch, outerResolver).RowValues;
        var schema = this.Schema;
        return new SimulatedSqlResultSet(schema, this.ColumnNames, rows.Select(values =>
        {
            var bytes = RowEncoder.EncodeRow(schema, values.AsSpan(0, schema.Length));
            if (values.Length > schema.Length && !values[schema.Length].IsNull)
            {
                var (page, slot) = RowLocator.Unpack(values[schema.Length].AsInt64);
                addresses.Record(bytes, page, slot);
            }
            return bytes;
        }))
        { ColumnNullability = this.ColumnNullability, ColumnReportsNumeric = this.ColumnReportsNumeric, ColumnAliasTypes = this.ColumnAliasTypes, ColumnIdentitySources = this.ColumnIdentitySources, ColumnWireFlags = this.ColumnWireFlags, HiddenColumnCount = this.HiddenColumnCount, Browse = this.Browse };
    }

    /// <summary>
    /// Whether the plan yields a row — the question an emptiness probe asks,
    /// which is all <c>EXISTS</c> and a NULL left side of <c>IN</c> need. Real
    /// answers it without evaluating the body's projection, so
    /// <c>EXISTS (SELECT 1/0 FROM t)</c> is TRUE over a non-empty <c>t</c>
    /// (probed 2026-08-05 against SQL Server 2025); a plain
    /// SELECT-project-filter body without ORDER BY skips its projection here
    /// the same way, while its WHERE, joins and row limit still run.
    /// </summary>
    internal bool HasAnyRow(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var previous = batch.ExistenceProbe;
        batch.ExistenceProbe = this;
        try
        {
            return this.valueRowSource is { } values
                ? values(batch, outerResolver).Any()
                : this.rowSource!(batch, outerResolver).Any();
        }
        finally
        {
            batch.ExistenceProbe = previous;
        }
    }

    /// <summary>
    /// Creates a <see cref="Selection"/> from a series of tokens. Follows the
    /// lookahead contract documented on <see cref="ParserContext"/>: on
    /// return, <see cref="ParserContext.Token"/> is the first token not
    /// consumed by the SELECT (typically <c>;</c>, <c>)</c> for a derived
    /// table or subquery, or null at end of command).
    /// </summary>
    /// <param name="context">Manages the overall parsing state.</param>
    /// <param name="scope">Where the query sits in its statement, and the resolver for an enclosing query's columns.</param>
    /// <returns>The prepared plan; call <see cref="Execute"/> to materialize results.</returns>
    /// <exception cref="SimulatedSqlException">A variety of messages are possible for various problems with the command.</exception>
    /// <exception cref="NotSupportedException">A condition was encountered that may be valid but can't currently be parsed.</exception>
    public static Selection Parse(ParserContext context, QueryScope scope)
    {
        // A subquery is never constant, and the predicate forms that hold one
        // without routing it through Expression.Parse (EXISTS, IN (SELECT …),
        // the quantified comparisons) would otherwise leave an enclosing
        // constant-fold frame believing every operand was a literal.
        context.FoldableArguments = false;
        context.QueriesParsed++;
        return ParseQueryExpression(context, scope);
    }

    /// <summary>
    /// Whether the query <paramref name="scope"/> places is a view's or inline
    /// function's own defining query as its <c>CREATE</c> parses it.
    /// </summary>
    private static bool DefinesModuleQuery(ParserContext context, QueryScope scope) =>
        context.DefiningModuleQuery != DefiningModuleQuery.None
        && scope.Position is QueryPosition.Statement or QueryPosition.ParenthesizedModuleBody;

    /// <summary>
    /// Parses a full query expression: a chain of set-op-combined SELECT
    /// branches optionally followed by a top-level ORDER BY. Set-op
    /// precedence: <c>INTERSECT</c> binds tighter than <c>UNION</c> /
    /// <c>EXCEPT</c> (which are at the same level, left-to-right).
    /// </summary>
    private static Selection ParseQueryExpression(ParserContext context, QueryScope scope)
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
            context.PendingBindError = null;
        }

        var sequenceDrawsBefore = context.SequenceDrawsParsed;
        var unwindowedSequenceDrawsBefore = context.UnwindowedSequenceDrawsParsed;
        var inlinedCallsBefore = context.Batch.InlinedCalls?.Calls.Count ?? 0;
        var combined = ParseUnionExceptChain(context, scope);

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
            ConsumeOffsetFetch(context, topLevelTail, scope);
            if (topLevelTail.OffsetExpression is not null && context.SequenceDrawsParsed > sequenceDrawsBefore)
                throw SimulatedSqlException.NextValueForNotAllowedWithRowLimit();
            // An ordered set operation inlines none of its branches' calls.
            if (combined.IsSetOperationResult)
                context.Batch.InlinedCalls?.DropFrom(inlinedCallsBefore);
            combined = ApplyTopLevelOrderBy(combined, orderBy, topLevelTail.OffsetExpression, topLevelTail.FetchExpression);
            bareProjectionStatement = false;
        }

        // Trailing FOR JSON { PATH | AUTO } [, options]: wraps the combined
        // result in a single-column JSON-string serializer. Sits where FOR XML
        // / FOR BROWSE do (after ORDER BY / OFFSET-FETCH, before OPTION); a
        // non-JSON FOR clause is left in place for the downstream Msg 102.
        var beforeForClauses = combined;
        if (scope.RefusesTrailingClauses && context.Token is ReservedKeyword { Keyword: Keyword.For } forKeyword)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(forKeyword);
        combined = ParseOptionalForJson(context, combined, scope);

        // Trailing FOR XML { RAW | AUTO | PATH } [, ELEMENTS …] [, ROOT …]:
        // wraps the result in a single-column xml serializer. Sits in the same
        // slot; a non-XML FOR clause is left in place for the downstream Msg 102.
        combined = ParseOptionalForXml(context, combined, scope);
        if (!ReferenceEquals(combined, beforeForClauses))
        {
            bareProjectionStatement = false;
            // Simple parameterization reads the query under the clause.
            combined.SimplyParameterizable = beforeForClauses.SimplyParameterizable;
        }

        // FOR BROWSE — the statement's own query in browse mode, which the
        // SELECT dispatch answers by reading the statement again as a browse
        // statement (see ParserContext.ForBrowseSeen); refused over a set
        // operation (Msg 198, probed 2026-09-26).
        if (scope.Position == QueryPosition.Statement && context.Token is ReservedKeyword { Keyword: Keyword.For })
        {
            var atFor = context.SaveCheckpoint();
            if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Browse })
            {
                if (combined.IsSetOperationResult)
                    throw SimulatedSqlException.BrowseModeWithSetOperator();
                context.MoveNextOptional();
                context.ForBrowseSeen = true;
                bareProjectionStatement = false;
            }
            else
            {
                context.RestoreCheckpoint(atFor);
            }
        }

        // OPTION (hint [, …]) — statement-level hint clause. Parsed as a
        // closed-list per Selection.Hints.cs; MAXRECURSION applies to in-
        // scope recursive CTEs, everything else recognized is discarded
        // (the simulator has nothing to dispatch on a hint against).
        // Only the statement's own query takes one: a subquery's, a derived
        // table's, a CTE's or an EXISTS test's is Msg 156 at the keyword, and
        // so is a set operator or a second OPTION after it, while a FOR XML /
        // FOR JSON clause may follow it (probed 2026-10-05 against SQL Server
        // 2025).
        OptionClause? optionClause = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Option } optionKeyword)
        {
            if (DefinesModuleQuery(context, scope) || scope.Position is QueryPosition.Derived or QueryPosition.Subquery or QueryPosition.Exists)
                throw SimulatedSqlException.SyntaxErrorNearKeyword(optionKeyword);
            optionClause = ParseOptionClause(context);
            bareProjectionStatement = false;
            context.SimpleParameterizationBlocked = true;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect or Keyword.Option } trailing)
                throw SimulatedSqlException.SyntaxErrorNearKeyword(trailing);
            if (ReferenceEquals(combined, beforeForClauses) && context.Token is ReservedKeyword { Keyword: Keyword.For })
            {
                combined = ParseOptionalForXml(context, ParseOptionalForJson(context, combined, scope), scope);
                if (!ReferenceEquals(combined, beforeForClauses))
                    combined.SimplyParameterizable = beforeForClauses.SimplyParameterizable;
            }
        }
        // The statement's hints are settled where its outermost query ends.
        var endsStatement = scope.Position switch
        {
            QueryPosition.Statement => ownsSecurableSink || context.DefiningModuleQuery != DefiningModuleQuery.None,
            QueryPosition.InsertSource or QueryPosition.ParenthesizedInsertSource or QueryPosition.Inlined => true,
            _ => false,
        };
        if (endsStatement)
            SettleStatementHints(context, optionClause);

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
            if (context.PendingBindError is { } pending)
            {
                context.PendingBindError = null;
                throw context.Token is Numeric or Literal
                    ? SimulatedSqlException.SyntaxErrorNear(context)
                    : pending;
            }
        }

        // A user function a query calls opens an implicit transaction; a
        // SELECT that only assigns variables and reads no FROM doesn't.
        if (context.Batch.CurrentStatement.CallsUserFunction && !combined.IsAssignmentOnly)
            context.Batch.BeginImplicitTransaction();

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
    private static Selection ParseUnionExceptChain(ParserContext context, QueryScope scope)
    {
        using var rejection = ParserScope.Save(ref context.NextValueForRejection);
        var sequenceDrawsBefore = context.SequenceDrawsParsed;
        return ParseUnionExceptChainCore(context, scope, sequenceDrawsBefore);
    }

    private static Selection ParseUnionExceptChainCore(ParserContext context, QueryScope scope, int sequenceDrawsBefore)
    {
        var left = ParseIntersectChain(context, scope, isFirstBranch: true);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Union or Keyword.Except } op)
        {
            SettleBranchOrdering(context, scope, left, op);
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
                if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.All })
                    throw SimulatedSqlException.SetOperatorAllNotSupported("EXCEPT", 2);
            }

            var right = ParseIntersectChain(context, scope, isFirstBranch: false);
            SettleBranchOrdering(context, scope, right, setOperator: null);
            RecordSetOperationShape(context);
            left = CombineSetOps(left, right, kind, scope.NamesOutputCollation);
        }
        return left;
    }

    /// <summary>
    /// Whether each branch of a set operation in <paramref name="scope"/> may
    /// carry an <c>ORDER BY</c> of its own, which then orders that branch alone
    /// — a derived table's, a CTE's, an <c>APPLY</c> body's, a subquery's, and a
    /// view or inline function body's — so a trailing <c>ORDER BY … OFFSET …</c>
    /// there pages the last branch rather than the combined rows: <c>(SELECT
    /// 3 UNION ALL SELECT 2 UNION ALL SELECT 1 ORDER BY 1 OFFSET 0 ROWS FETCH
    /// NEXT 1 ROWS ONLY) d</c> is three rows, and <c>SELECT TOP 1 k FROM u
    /// ORDER BY k UNION SELECT 3</c> reads as two branches. A statement's own
    /// query, an <c>INSERT</c>'s source and a cursor's query order the combined
    /// rows (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static bool BranchesOrderThemselves(ParserContext context, QueryScope scope) =>
        scope.Position is QueryPosition.Inlined or QueryPosition.Derived or QueryPosition.Subquery or QueryPosition.Exists
        || DefinesModuleQuery(context, scope);

    /// <summary>
    /// Judges the <c>ORDER BY</c> a branch of a set operation carries, once the
    /// branch is known to be one: where branches order themselves
    /// (<see cref="BranchesOrderThemselves"/>) it is allowed beside a <c>TOP</c>
    /// or <c>OFFSET</c> and Msg 1033 without one; elsewhere a set operator after
    /// it is Msg 156 at the operator as written. <paramref name="setOperator"/>
    /// is the operator that follows the branch, null for the last branch.
    /// </summary>
    internal static void SettleBranchOrdering(ParserContext context, QueryScope scope, Selection branch, ReservedKeyword? setOperator)
    {
        if (!branch.HasOrderBy || branch.OrdersOwnRows)
            return;
        if (BranchesOrderThemselves(context, scope))
        {
            if (!branch.HasTopOrOffsetOrFetch)
                throw SimulatedSqlException.OrderByInvalidInCte();
            branch.OrdersOwnRows = true;
        }
        else if (setOperator is not null)
        {
            throw SimulatedSqlException.SyntaxErrorNearKeyword(setOperator);
        }
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
        context.RaiseNextValueForFloor(NextValueForScope.Deduplicating);
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
    internal static Selection ParseIntersectChain(ParserContext context, QueryScope scope, bool isFirstBranch)
    {
        using var rejection = ParserScope.Save(ref context.NextValueForRejection);
        var sequenceDrawsBefore = context.SequenceDrawsParsed;
        return ParseIntersectChainCore(context, scope, isFirstBranch, sequenceDrawsBefore);
    }

    private static Selection ParseIntersectChainCore(ParserContext context, QueryScope scope, bool isFirstBranch, int sequenceDrawsBefore)
    {
        var ordersBranches = BranchesOrderThemselves(context, scope);
        var left = ParseSetOpBranch(context, scope, allowOrderBy: isFirstBranch || ordersBranches);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Intersect } intersect)
        {
            SettleBranchOrdering(context, scope, left, intersect);
            RejectSequenceDrawUnderSetOperator(context, sequenceDrawsBefore);
            if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.All })
                throw SimulatedSqlException.SetOperatorAllNotSupported("INTERSECT", 1);
            var right = ParseSetOpBranch(context, scope, allowOrderBy: ordersBranches);
            SettleBranchOrdering(context, scope, right, setOperator: null);
            RecordSetOperationShape(context);
            left = CombineSetOps(left, right, SetOpKind.Intersect, scope.NamesOutputCollation);
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
    private static Selection ParseSetOpBranch(ParserContext context, QueryScope scope, bool allowOrderBy)
    {
        if (context.Token is not Operator { Character: '(' })
            return ParseSingleSelectStatement(context, scope, allowOrderBy);

        // Each parenthesis recurses, so a deep stack of them meets the probe
        // deep expressions do (Msg 8631) rather than the process's limit.
        Expression.EnsureParseStack();

        // Only a query opens inside: `(VALUES …)`, `(WITH …)` and a value are
        // the syntax error at their first token, which is also how a
        // statement `(-1)` fails (probed 2026-10-01 against SQL Server 2025).
        if (context.GetNextRequired() is not (ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: '(' }))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var parenthesized = scope.InParentheses();
        var inner = ParseUnionExceptChain(context, parenthesized);

        // An ORDER BY after a chain the parentheses hold orders that chain,
        // as it would a statement; at a statement it is refused as any ORDER
        // BY in the parentheses is.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Order } orderKeyword)
        {
            if (RefusesParenthesizedOrderBy(context, parenthesized))
                throw SimulatedSqlException.SyntaxErrorNearKeyword(orderKeyword);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var orderBy = new List<OrderBySpec>();
            ParseOrderByItems(context, orderBy);
            var tail = new FromClause();
            ConsumeOffsetFetch(context, tail, scope);
            inner = ApplyTopLevelOrderBy(inner, orderBy, tail.OffsetExpression, tail.FetchExpression);
        }

        // A nested query's parenthesized branch may order the rows its TOP or
        // OFFSET takes; without one the ORDER BY is Msg 1033.
        if (inner.HasOrderBy)
        {
            if (!inner.HasTopOrOffsetOrFetch)
                throw SimulatedSqlException.OrderByInvalidInCte();
            inner.OrdersOwnRows = true;
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return inner;
    }

    /// <summary>
    /// Whether an <c>ORDER BY</c> inside <paramref name="scope"/>'s parentheses
    /// is refused (<see cref="QueryScope.ParenthesizedInStatement"/>): a
    /// statement's own query, but not a view or inline function body, which
    /// parses at that position too.
    /// </summary>
    private static bool RefusesParenthesizedOrderBy(ParserContext context, QueryScope scope) =>
        scope.ParenthesizedInStatement && !context.BindingViewDefinition && context.Batch.UdfFrame is null;

    /// <summary>
    /// Whether the <c>(</c> under the cursor, the first token inside another
    /// parenthesis, opens a query that a set operator follows — the first
    /// branch of <c>((SELECT 1) UNION (SELECT 2))</c> — or, with
    /// <paramref name="closeCounts"/>, the enclosing parenthesis's close, as
    /// in <c>((SELECT 1))</c>. Restores the cursor; a site reaching the inner
    /// <c>(</c> anyway asks here so an ordinary grouping pays a token test.
    /// </summary>
    /// <remarks>
    /// One forward pass: the innermost of the leading parentheses holds a
    /// query when a <c>SELECT</c> opens it, and each one out holds one when a
    /// set operator or its own close follows the close of the one inside it,
    /// the enclosing parenthesis's own close counting only with
    /// <paramref name="closeCounts"/>. Iterative, so no depth of nesting
    /// reaches the stack.
    /// </remarks>
    internal static bool LeadsParenthesizedQuery(ParserContext context, bool closeCounts)
    {
        var checkpoint = context.SaveCheckpoint();
        // Levels count from the cursor's parenthesis as 1; the enclosing one,
        // whose answer this is, is 0.
        var depth = 1;
        var token = context.GetNextOptional();
        while (token is Operator { Character: '(' })
        {
            depth++;
            token = context.GetNextOptional();
        }

        // Every level from queryLevel in holds a query.
        var queryLevel = depth;
        var opens = token is ReservedKeyword { Keyword: Keyword.Select };
        while (opens && queryLevel > 0)
        {
            if (context.GetNextOptional() is not { } current)
            {
                opens = false;
                break;
            }
            if (current is Operator { Character: '(' })
            {
                depth++;
                continue;
            }
            if (current is not Operator { Character: ')' } || --depth >= queryLevel)
                continue;

            // Level queryLevel just closed: what follows settles the one around it.
            while (true)
            {
                var next = context.GetNextOptional();
                if (next is ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect })
                {
                    queryLevel--;
                    break;
                }
                if (next is not Operator { Character: ')' } || (queryLevel == 1 && !closeCounts))
                {
                    opens = false;
                    break;
                }
                if (--queryLevel <= 0)
                    break;
                depth--;
            }
        }
        context.RestoreCheckpoint(checkpoint);
        return opens;
    }

    /// <summary>
    /// How many of the parentheses opening a module body wrap all of it, which
    /// the body's text leaves out: in <c>((SELECT 1) UNION (SELECT 2))</c> one
    /// does, in <c>(SELECT 1) UNION (SELECT 2)</c> none — the first branch's
    /// parentheses belong to the body (probed 2026-10-01 against SQL Server
    /// 2025). The outermost wraps unless a set operator follows its close; each
    /// further one only when the enclosing one's close follows its own.
    /// Restores the cursor.
    /// </summary>
    internal static int CountWrappingParentheses(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var leading = 0;
        var token = context.Token;
        while (token is Operator { Character: '(' })
        {
            leading++;
            token = context.GetNextOptional();
        }

        // One pass: the leading parentheses close innermost first, and those
        // wrapping the body are the ones whose closes run back to back into
        // the outermost's — which no set operator may follow.
        var depth = leading;
        var open = leading;
        var run = 0;
        var afterLeadingClose = false;
        while (open > 0 && token is not null)
        {
            var closesLeading = false;
            if (token is Operator { Character: '(' })
            {
                depth++;
            }
            else if (token is Operator { Character: ')' } && --depth < open)
            {
                run = afterLeadingClose ? run + 1 : 1;
                open = depth;
                closesLeading = true;
            }
            afterLeadingClose = closesLeading;
            token = context.GetNextOptional();
        }
        var count = open == 0 && token is not ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect } ? run : 0;
        context.RestoreCheckpoint(checkpoint);
        return count;
    }

    /// <summary>
    /// Whether the <c>WITH</c> under the cursor opens a view's trailing
    /// <c>WITH CHECK OPTION</c>, which ends the body's query rather than
    /// starting a CTE-prefixed statement or a table hint. Restores the cursor.
    /// </summary>
    internal static bool AtWithCheckOption(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextOptional();
        var opensCheckOption = context.Token is ReservedKeyword { Keyword: Keyword.Check };
        context.RestoreCheckpoint(checkpoint);
        return opensCheckOption;
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
    internal static Selection ParseSingleSelectStatement(ParserContext context, QueryScope scope, bool allowOrderBy)
    {
        // Each branch gets its own collector scope: aggregates and window
        // functions parsed inside the projection / HAVING register into these
        // lists, and the executor uses the populated lists to switch into
        // aggregate or windowed-projection mode. ParseInner installs this
        // scope's FROM sources as the outer resolvers and scope sources and
        // raises the branch's NEXT VALUE FOR floor for DISTINCT / TOP; the
        // frame puts all of them back for the enclosing scope on every exit
        // path. A nested query body owns its own name scope, so the frame also
        // suspends an enclosing FROM source's sibling collector.
        var aggregates = new List<AggregateExpression>();
        var windows = new List<WindowExpression>();
        using var queryBlock = context.EnterQueryBlock(aggregates, windows);
        using var inliningBlock = ParserScope.Enter(ref context.InliningBlock, context.Batch.InlinedCalls?.OpenBlock() ?? 0);
        using var inliningClause = ParserScope.Enter(ref context.InliningClause, InliningClause.Other);
        var bindErrors = context.Batch.BindErrors;
        bindErrors?.OpenScope(context.Token);
        try
        {
            var parsed = ParseInner(context, scope, aggregates, windows, allowOrderBy);
            // Settled where the ORDER BY is known; this is the backstop for a
            // path that returned without passing there.
            SettleDeferredNextValueRefs(context, orderBy: false, constantOrderBy: false, offset: false, projectionStart: 0, projectionEnd: 0);
            return parsed;
        }
        finally
        {
            bindErrors?.CloseScope(context.Token);
        }
    }

    /// <summary>
    /// Whether the query block can read a base-table column in a predicate at
    /// all — a base table among its sources and some clause that loads
    /// statistics — so the common predicate-free or table-free block skips
    /// gathering operands.
    /// </summary>
    private static bool ReadsPredicateColumns(List<FromSource> sources, FromClause fromClause, List<JoinSpec> joins, bool distinct)
    {
        if (!sources.Exists(static source => source.BackingTable is { IsTableVariable: false } || source.BackingView is { BaseTable: not null }))
            return false;
        return distinct || fromClause.Excluders.Count > 0 || fromClause.Having is not null || fromClause.GroupByAllFilter is not null
            || fromClause.AllGroupingExpressions.Count > 0 || joins.Exists(static join => join.OnPredicate is not null);
    }

    /// <summary>
    /// The expressions whose column references the optimizer loads statistics
    /// for: the WHERE's and HAVING's operands, every join's ON, the GROUP BY,
    /// and the select list of a DISTINCT.
    /// </summary>
    private static List<Expression> PredicateOperands(FromClause fromClause, List<JoinSpec> joins, List<Expression>? distinctList)
    {
        var operands = new List<Expression>();
        foreach (var excluder in fromClause.Excluders)
            excluder.VisitOperandExpressions(operands.Add);
        fromClause.Having?.VisitOperandExpressions(operands.Add);
        if (fromClause.GroupByAllFilter is { } groupByAllFilter)
        {
            foreach (var excluder in groupByAllFilter)
                excluder.VisitOperandExpressions(operands.Add);
        }
        foreach (var join in joins)
            join.OnPredicate?.VisitOperandExpressions(operands.Add);
        operands.AddRange(fromClause.AllGroupingExpressions);
        if (distinctList is not null)
            operands.AddRange(distinctList);
        return operands;
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
        /// The slice of <see cref="ParserContext.DeferredNextValueRefs"/> the
        /// select list itself parsed, which real binds after every clause
        /// (<c>-1</c> until the list ends).
        /// </summary>
        public int ProjectionRefsStart;
        public int ProjectionRefsEnd = -1;

        /// <summary>
        /// The aggregates the WHERE clause's own parse registered with this
        /// query, which it may hold only when they read an enclosing query's
        /// columns and so move there (Msg 147 otherwise).
        /// </summary>
        public List<AggregateExpression>? WhereAggregates;

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

        /// <summary>
        /// True when the GROUP BY spelled a <c>ROLLUP</c>, <c>CUBE</c>,
        /// <c>GROUPING SETS</c> or legacy <c>WITH ROLLUP</c> / <c>WITH CUBE</c>
        /// — whatever sets that expands to. Real then reports every projected
        /// column reference nullable in the result metadata, even one present
        /// in every set (<c>GROUPING SETS ((a))</c>, <c>GROUP BY a, ROLLUP(b)</c>
        /// both report <c>a</c> nullable; probed 2026-09-23).
        /// </summary>
        public bool GroupingSetsWritten;

        public BooleanExpression? Having;

        /// <summary>
        /// True for <c>GROUP BY ALL</c>: every group the source produces is
        /// kept, whatever the <c>WHERE</c> clause does to its rows.
        /// </summary>
        public bool GroupByAll;

        /// <summary>
        /// For <c>GROUP BY ALL</c> over a <c>WHERE</c>, the WHERE's conjuncts,
        /// moved out of <see cref="Excluders"/> so that every row reaches the
        /// grouping and the plan's seek / pushdown machinery reads the whole
        /// source: a row failing them still forms its group but feeds none of
        /// the aggregates, so the group's <c>COUNT</c> is 0 and every other
        /// aggregate NULL. Null otherwise.
        /// </summary>
        public List<BooleanExpression>? GroupByAllFilter;

        /// <summary>
        /// Whether this query's own FROM clause reads a linked server (see
        /// <see cref="ParserContext.RemoteSourcesParsed"/>).
        /// </summary>
        public bool ReadsRemoteSource;

        /// <summary>
        /// The sources a WHERE clause's <c>MATCH</c> bound, which lead a bare
        /// <c>SELECT *</c> in real's order rather than the FROM clause's.
        /// </summary>
        public Expressions.MatchScope? Match;

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
                GroupByAll = this.GroupByAll,
                GroupByAllFilter = this.GroupByAllFilter,
                GroupingSetsWritten = this.GroupingSetsWritten,
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
    private static int? ResolveRowCountLimit(Expression? expression, RowLimitKind kind, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver = null)
    {
        if (expression is null)
            return null;
        // sp_describe_undeclared_parameters types a row count bigint (probed
        // 2026-09-26 against SQL Server 2025).
        if (UndeclaredParameterDeduction.NoteExact(expression, SqlType.BigInt))
            return null;
        // A module body binds without running, so an operand naming a parameter
        // has no value to read — its slot is empty and would report Msg 1060 as
        // if it were NULL. Real settles that one from the operand's declared
        // type, which is why `TOP (@rows)` over an `int` parameter creates while
        // the `nvarchar` and `decimal(5, 2)` spellings are refused at CREATE.
        // A written constant keeps the ordinary value check, since real refuses
        // `TOP (NULL)` and `TOP (1.5)` at CREATE too (probe-confirmed).
        if (batch.CreateTimeBinding && !expression.IsWrittenConstant)
        {
            var declared = expression.GetSqlType(batch, name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
            return IsRowCountType(declared, expression) ? null
                : kind == RowLimitKind.Offset ? throw SimulatedSqlException.OffsetRequiresInteger()
                : throw SimulatedSqlException.TopFetchRequiresInteger();
        }

        var resolved = expression.Run(new RuntimeContext(name => outerResolver is null ? throw SimulatedSqlException.ColumnReferenceNotAllowed(name) : outerResolver(name), batch));
        if (kind == RowLimitKind.Offset && (resolved.IsNull || !IsRowCountType(resolved.Type, expression)))
            throw SimulatedSqlException.OffsetRequiresInteger();
        var count = ClampRowCount(resolved, expression);
        // What a written count is settles while compiling: a FETCH below one is
        // Msg 10744 there, where a variable's is a run-time Msg 127 and a zero
        // variable fetches nothing (probed 2026-09-24 against SQL Server 2025).
        return kind switch
        {
            RowLimitKind.Offset when count < 0 => throw SimulatedSqlException.OffsetMustNotBeNegative(),
            RowLimitKind.Fetch when count < 1 && expression.IsWrittenConstant => throw SimulatedSqlException.FetchMustBeGreaterThanZero(),
            _ when count < 0 => throw SimulatedSqlException.TopRowCountMustNotBeNegative(),
            _ => count,
        };
    }

    /// <summary>
    /// A row count may read an enclosing query's columns — a correlated
    /// subquery's or an <c>APPLY</c> body's <c>TOP (t.g)</c> counts per outer
    /// row, Msg 1014 for a NULL and 127 for a negative (probed 2026-10-01
    /// against SQL Server 2025) — so an operand reading columns has no value
    /// to check while parsing. Each column must resolve in the enclosing
    /// scope (Msg 4115 otherwise) and the operand's type must count; answers
    /// whether the operand read any column, leaving the value to the run.
    /// </summary>
    private static bool ReadsOuterColumns(Expression expression, bool percent, ParserContext context, Func<MultiPartName, SqlType>? scopeOuter, RowLimitKind kind = RowLimitKind.Top)
    {
        MultiPartName? first = null;
        expression.VisitColumnReferences(name => first ??= name);
        if (first is not { } column)
            return false;
        // A correlated subquery reads its enclosing query through the parser's
        // resolver, an APPLY body its left side through the scope's.
        var outer = context.OuterTypeResolver ?? scopeOuter ?? throw SimulatedSqlException.ColumnReferenceNotAllowed(column);
        SqlType declared;
        try
        {
            declared = expression.GetSqlType(context.Batch, outer);
        }
        catch (SimulatedSqlException) when (scopeOuter is not null && !ReferenceEquals(outer, scopeOuter))
        {
            try
            {
                declared = expression.GetSqlType(context.Batch, scopeOuter);
            }
            catch (SimulatedSqlException)
            {
                throw SimulatedSqlException.ColumnReferenceNotAllowed(column);
            }
        }
        catch (SimulatedSqlException)
        {
            throw SimulatedSqlException.ColumnReferenceNotAllowed(column);
        }
        return IsRowCountType(declared, expression) || percent
            ? true
            : kind == RowLimitKind.Offset ? throw SimulatedSqlException.OffsetRequiresInteger()
            : throw SimulatedSqlException.TopFetchRequiresInteger();
    }

    /// <summary>
    /// Whether a row count's operand has a type real counts with: an integer
    /// other than <c>bit</c>, or an exact numeric at scale 0 that real names
    /// <c>numeric</c> — a literal (<c>2.</c>, <c>9999999999</c>), a
    /// <c>CAST … AS numeric(5, 0)</c> or arithmetic over one — where the same
    /// value typed <c>decimal(5, 0)</c>, a computation over that or a variable
    /// declared either way is Msg 1060 (probed 2026-10-01 against SQL Server
    /// 2025).
    /// </summary>
    private static bool IsRowCountType(SqlType type, Expression expression) =>
        (SqlType.IsIntegerCategory(type) && type is not BitSqlType)
        || (type is DecimalSqlType { scale: 0 } && expression.ResultReportsNumeric);

    /// <summary>
    /// The error a NULL <c>TOP</c> / <c>FETCH</c> count raises: an integer
    /// that turned out NULL while running is Msg 1014, and a written NULL —
    /// or one of another type — the compile-time Msg 1060 (probed 2026-09-24
    /// against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException NullRowCount(SqlValue resolved, Expression expression) =>
        !expression.IsWrittenConstant && SqlType.IsIntegerCategory(resolved.Type)
            ? SimulatedSqlException.TopClauseInvalidValue()
            : SimulatedSqlException.TopFetchRequiresInteger();

    /// <summary>
    /// The row count a <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> operand
    /// yields. Real accepts the types <see cref="IsRowCountType"/> names — an
    /// integer literal past int's range is <c>numeric(digit_count, 0)</c>, so
    /// <c>TOP (9999999999)</c> is an ordinary accepted row count — narrowing
    /// the operand to <c>bigint</c>
    /// (a 20-digit literal overflows there with Msg 8115 naming
    /// <c>bigint</c>). A fractional scale is the grammar's Msg 1060, as is
    /// any other family; NULL is <see cref="NullRowCount"/>'s. The result clamps to <c>int</c>: no
    /// simulated row source reaches 2^31 rows, so a wider cap or offset is
    /// indistinguishable from the clamp.
    /// </summary>
    private static int ClampRowCount(SqlValue resolved, Expression expression)
    {
        if (resolved.IsNull)
            throw NullRowCount(resolved, expression);
        if (!IsRowCountType(resolved.Type, expression))
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
    private static double ResolveTopPercentValue(Expression expression, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver = null)
    {
        var resolved = expression.Run(new RuntimeContext(name => outerResolver is null ? throw SimulatedSqlException.ColumnReferenceNotAllowed(name) : outerResolver(name), batch));
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
        while (c < rows.Count && SortOrderKeys(keysOf(rows[c]), boundary, orderBy) == 0)
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
        Expression expression;
        using (context.EnterNextValueForScope(NextValueForScope.Clause))
        {
            expression = Expression.Parse(context);
        }
        var percent = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Percent })
        {
            percent = true;
            context.MoveNextRequired();
        }
        var limit = new DmlTopLimit(expression, percent);
        // A written constant is judged while compiling, so its error ends the
        // batch with no Msg 3621 (probed 2026-09-28 against SQL Server 2025:
        // `TOP (-1)`, `TOP (1.5)`, `TOP (101) PERCENT`) — unless the statement
        // waits to bind with an object the batch creates, which the walk
        // learns only once it has read the target, for any statement but an
        // INSERT (probed 2026-10-06). Raised as the deferred statement runs,
        // it takes the statement's first line and ends the batch with no Msg
        // 3621, uncaught by a TRY around it.
        if (expression.IsWrittenConstant)
        {
            try
            {
                _ = ResolveDmlTopCap(limit, int.MaxValue, context.Batch);
            }
            catch (SimulatedSqlException refused) when (context.Batch.IsSkipping && context.Batch.CurrentStatement.StatementVerb != "INSERT")
            {
                // Skipped as the batch runs — an untaken branch — the statement
                // is one the compile deferred, which nothing compiles now.
                if (context.Batch.CreateTimeBinding)
                {
                    refused.ResolveDiagnostics(context.Token?.LineNumber ?? context.Batch.CurrentStatement.StartLine, context.Batch.LineOffset, context.Batch.ErrorProcedureName);
                    context.Batch.CurrentStatement.PendingCompileRefusal = refused;
                }
            }
        }
        return limit;
    }

    /// <summary>
    /// Resolves a DML <c>TOP</c> limit to a concrete row cap given the number
    /// of candidate rows already collected. Validates the value the way SQL
    /// Server does: a non-PERCENT value must be a non-negative integer
    /// (Msg 1060 for non-integer, <see cref="NullRowCount"/> for NULL, Msg 127
    /// for negative); a PERCENT
    /// value must be numeric in [0, 100] (Msg 1031, Msg 1014 for NULL), and
    /// the cap is <c>ceil(candidateCount * pct / 100)</c> — probe-confirmed
    /// against SQL Server 2025.
    /// </summary>
    internal static int ResolveDmlTopCap(DmlTopLimit limit, int candidateCount, BatchContext batch)
    {
        try
        {
            return ResolveDmlTopCapCore(limit, candidateCount, batch);
        }
        catch (SimulatedSqlException ex) when (!limit.Expression.IsWrittenConstant || !batch.IsSkipping)
        {
            // A value read while running reports at the statement's first line,
            // where a class-15 error otherwise takes the parser's current one.
            foreach (var error in ex.Errors)
                error.LineNumber = batch.CurrentStatement.StartLine;
            ex.RefusedRecompilingDeferred = limit.Expression.IsWrittenConstant;
            throw;
        }
    }

    private static int ResolveDmlTopCapCore(DmlTopLimit limit, int candidateCount, BatchContext batch)
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
        var count = resolved.IsNull ? throw NullRowCount(resolved, limit.Expression)
            : !IsRowCountType(resolved.Type, limit.Expression) ? throw SimulatedSqlException.TopFetchRequiresInteger()
            : resolved.CoerceTo(SqlType.BigInt).AsInt64;
        return count < 0
            ? throw SimulatedSqlException.TopRowCountMustNotBeNegative()
            : count < candidateCount ? (int)count : candidateCount;
    }

    private static Selection ParseInner(ParserContext context, QueryScope scope, List<AggregateExpression> aggregates, List<WindowExpression> windows, bool allowOrderBy)
    {
        // A query block owns its named windows, and its select list takes a
        // windowed function wherever the block itself sits — a subquery in an
        // enclosing WHERE included (probed 2026-09-29 against SQL Server 2025).
        using var windowScope = ParserScope.Enter(ref context.NamedWindowScope, (context.PendingNamedWindows.Count, context.NamedWindowDefinitions.Count));
        using var allowsWindows = ParserScope.Enter(ref context.AllowsWindowExpressions, true);
        using var inWhere = ParserScope.Enter(ref context.InWhereClause, false);
        // A body a browse statement flattens takes the arming its FROM source
        // left, which the first block to begin consumes; anything nested in it
        // starts disarmed.
        var flattensForBrowse = context.BrowseFlatten && scope.Position is QueryPosition.Derived or QueryPosition.Inlined;
        context.BrowseFlatten = false;
        using var browseFromScope = ParserScope.Enter(ref context.BrowseFlattenFrom, false);
        using var browseBody = ParserScope.Enter(ref context.BrowseFlattenBody, flattensForBrowse);
        return ParseQueryBlock(context, scope, aggregates, windows, allowOrderBy);
    }

    private static Selection ParseQueryBlock(ParserContext context, QueryScope scope, List<AggregateExpression> aggregates, List<WindowExpression> windows, bool allowOrderBy)
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

        // A DISTINCT statement's Msg 11721 outranks the TOP count's own clause refusal.
        if (distinct)
            context.RaiseNextValueForFloor(NextValueForScope.Deduplicating);

        if (firstToken is ReservedKeyword { Keyword: Keyword.Top })
        {
            context.Batch.BindErrors?.EnterClause(firstToken, BindClause.Top);
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
            using (context.EnterNextValueForScope(NextValueForScope.Clause))
            {
                using var topCountScope = ParserScope.Enter(ref context.InTopCount, true);
                context.RecursiveBranchConstructs.TopOrOffset = true;
                // A string literal is no legacy count either (`TOP '1'`, Msg 102
                // near it; probed 2026-09-24).
                // Nor is a variable: `TOP @n` is Msg 102 near it, where `TOP (@n)`
                // counts (probed 2026-10-01 against SQL Server 2025).
                if (context.MoveNextRequiredReturnSelf().Token is Operator { Character: '+' or '-' or '~' } or Literal { Value.Type.Category: SqlTypeCategory.String } or AtPrefixedString)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                topExpression = Expression.ParsePrimary(context);
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
            // SQL Server's compile-time rejection. A module body binding a
            // parameter has no value to check (see ResolveRowCountLimit).
            // A count drawing from a sequence is refused once the statement's
            // clauses are read, and evaluating it here would draw.
            var drawsSequence = false;
            topExpression.Walk((node, _) => !(drawsSequence |= node is NextValueFor));
            if (!drawsSequence && !ReadsOuterColumns(topExpression, topPercent, context, scope.OuterTypeResolver))
            {
                if (!topPercent)
                    _ = ResolveRowCountLimit(topExpression, RowLimitKind.Top, context.Batch);
                else if (!context.Batch.CreateTimeBinding || topExpression.IsWrittenConstant)
                    _ = ResolveTopPercentValue(topExpression, context.Batch);
            }
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
            context.RaiseNextValueForFloor(NextValueForScope.Deduplicating);
        else if (topExpression is not null || context.Connection.RowCountLimit > 0)
            context.RaiseNextValueForFloor(NextValueForScope.RowLimited);

        // Msg 11723 is a property of the finished statement rather than of the
        // reference's own position, so it is settled below against this
        // snapshot once the ORDER BY has been read.
        var sequenceDrawsBefore = context.SequenceDrawsParsed;
        var unwindowedSequenceDrawsBefore = context.UnwindowedSequenceDrawsParsed;

        List<Expression> expressions = [];
        var fromClause = new FromClause();
        MultiPartName? intoTarget = null;
        DataSpaceClause? intoDataSpace = null;

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
        var browseFrom = context.BrowseFlattenBody || (scope.Position == QueryPosition.Statement && context.BrowseStatement);
        ParserContext.Checkpoint afterSources = default;
        context.Batch.BindErrors?.EnterClause(context.Token, BindClause.SelectList);
        if (FindOwnFromClause(context) is { } fromCheckpoint)
        {
            var selectListStart = context.SaveCheckpoint();
            context.RestoreCheckpoint(fromCheckpoint);
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.From);
            var candidateSources = new List<FromSource>();
            var candidateJoins = new List<JoinSpec>();
            try
            {
                var remoteSourcesBefore = context.RemoteSourcesParsed;
                using (ParserScope.Enter(ref context.BrowseFlattenFrom, browseFrom))
                    ParseSourcesAndJoins(context, scope, candidateSources, candidateJoins);
                afterSources = context.SaveCheckpoint();
                preParsedSources = candidateSources;
                preParsedJoins = candidateJoins;
                fromClause.ReadsRemoteSource = context.RemoteSourcesParsed > remoteSourcesBefore;
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
            if (preParsedSources is { } preParsed)
            {
                var scopeSources = preParsed.ToArray();
                context.OuterTypeResolver = name => ResolveColumnTypeAcrossSources(scopeSources, name, scope.OuterTypeResolver);
                if (context.Batch.Connection.Simulation.DeclaresDataMasks)
                {
                    var outerMask = context.OuterMaskResolver;
                    context.OuterMaskResolver = name => ScopedColumnMask(scopeSources, name, outerMask);
                }
                // A projection-level CONTAINS / FREETEXT (`CASE WHEN
                // CONTAINS(col, 'x') THEN …`) and a spatial column's property
                // form (`Location.Lat`) both bind against the same scope.
                context.ScopeSources = scopeSources;
            }
        }

        // No FROM of this statement's own bound (there is none, or the
        // speculative pre-pass above discarded it): install the enclosing
        // scope directly. A nested subquery reads its outer chain from
        // `context.OuterTypeResolver` — it never sees this parse's `scope` —
        // so leaving whatever the enclosing parse happened to have there is
        // what made a FROM-less APPLY body's
        // `WHERE EXISTS (… c.k …)` fail to bind against the APPLY's left side
        // (a body carrying its own FROM installed the chain and worked).
        if (preParsedSources is null && scope.OuterTypeResolver is not null)
            context.OuterTypeResolver = scope.OuterTypeResolver;

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
        // The sequences the assignments read, for Msg 11736.
        List<Schemas.Sequence>? assignedSequences = null;
        fromClause.ProjectionRefsStart = context.DeferredNextValueRefs?.Count ?? 0;
        using var selectList = ParserScope.Enter(ref context.InliningClause, InliningClause.SelectList);
        do
        {
            // A keyword standing where an element belongs means the list never
            // began (or a comma promised one that never arrived): real reports
            // Msg 156 naming that keyword, where the statement-boundary arms
            // below would otherwise end the projection and leave a short or
            // zero-column SELECT behind. End-of-input isn't a keyword, so a
            // bare SELECT keeps its Msg 102; a `;` there is Msg 102 naming the
            // `;` (`SELECT;`, `SELECT 1,;` — probe-confirmed 2026-09-23).
            if (elementExpected && context.Token is ReservedKeyword blocking && !CanBeginProjectionElement(blocking)
                && !(blocking.Keyword == Keyword.Identity && scope.AcceptsIdentityFunction))
            {
                throw SimulatedSqlException.SyntaxErrorNearKeyword(blocking);
            }
            if (elementExpected && context.Token is Operator { Character: ';' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            // The mirror case: this switch is re-entered after an alias was
            // taken, so a further value token is one too many for a single
            // element — `SELECT 1 xyz 2` is Msg 102 at the `2`, not a second
            // column. Only a comma or a clause keyword may follow a complete,
            // aliased element (probe-confirmed) — or a `(` outside
            // parentheses, which opens the next statement, a parenthesized
            // query, as it does after an unaliased element below.
            if (!elementExpected && context.Token is Operator { Character: '(' } && !scope.Parenthesized)
                goto ExitWhileTokenLoop;
            if (!elementExpected && StartsProjectionElement(context.Token) && !IsWindowClauseAhead(context) && context.Token is not UnquotedString { IsLabelDeclaration: true })
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
                case Name when IsWindowClauseAhead(context):
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
                // it can only mean a CTE-prefixed follow-up statement — unless
                // it opens a view's trailing WITH CHECK OPTION, which ends the
                // body. Real SQL Server raises Msg 319 here rather than the
                // generic Msg 156 from the catch-all below — telling the user
                // to separate statements with `;`. Checked before the general
                // statement-boundary case (which also treats WITH as a
                // boundary) so the more specific Msg 319 wins.
                case ReservedKeyword { Keyword: Keyword.With } when !scope.Parenthesized && AtWithCheckOption(context):
                    goto ExitWhileTokenLoop;
                case ReservedKeyword { Keyword: Keyword.With } when !scope.Parenthesized:
                    throw SimulatedSqlException.CteRequiresPrecedingSemicolon();

                // In a statement's own query, the start of another statement
                // terminates this SELECT and lets the dispatch loop pick up
                // where it left off. Real SQL Server allows back-to-back
                // statements without `;` between them; we mirror by stopping
                // the projection-list parse here. Inside parentheses these
                // keywords are still invalid — fall through to the generic
                // Msg 156 catch-all below.
                case Operator { Character: ';' } when !scope.Parenthesized:
                    goto ExitWhileTokenLoop;
                case ReservedKeyword statementStart when !scope.Parenthesized && Simulation.IsStatementBoundary(statementStart):
                    goto ExitWhileTokenLoop;

                // SELECT … INTO's IDENTITY(type [, seed, increment]) is a whole
                // item and takes a column alias, so anything but an alias after
                // it is a syntax error (probe-confirmed).
                case ReservedKeyword { Keyword: Keyword.Identity } when scope.AcceptsIdentityFunction:
                    expressions.Add(new IdentityFunction(context));
                    if (context.Token is not (Name or Literal { Value.Type.Category: SqlTypeCategory.String } or ReservedKeyword { Keyword: Keyword.As }))
                        throw context.Token is ReservedKeyword follower ? SimulatedSqlException.SyntaxErrorNearKeyword(follower) : SimulatedSqlException.SyntaxErrorNear(context);
                    break;

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
                    if (!scope.Parenthesized)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    goto ExitWhileTokenLoop;

                // Bare `*` as a projection element (the first thing after
                // SELECT, or the first thing after a comma). Within a
                // projected expression, `*` is the multiplication operator and
                // is handled by Expression.Parse's binary loop instead.
                case Operator { Character: '*' }:
                    RejectStarInSchemaBoundBody(context, scope, qualified: false);
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
                            Expression rhs;
                            assignedSequences ??= [];
                            using (ParserScope.Enter(ref context.SequenceCollector, assignedSequences))
                                rhs = Expression.Parse(context);
                            // One sequence drawn twice across a variable-assigning
                            // SELECT is refused as the statement runs, ending the
                            // batch (probed 2026-10-04 against SQL Server 2025).
                            if (!context.Batch.IsSkipping && assignedSequences.Count != assignedSequences.Distinct().Count())
                                throw SimulatedSqlException.NextValueForTwiceInAssignment();
                            expressions.Add(new AssignmentExpression(slot, rhs));
                        }
                        // The compound forms, `@v += expr` and its siblings, read
                        // the variable as each row assigns it, so a SELECT over a
                        // table accumulates: `SELECT @s += name FROM t`.
                        else if (context.Token is Operator { Character: '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' } compound
                            && context.GetNextOptional() is Operator { Character: '=' } equals
                            && equals.StartIndex == compound.EndIndex)
                        {
                            var slot = context.Batch.GetVariableSlot(atPrefixed.Value);
                            context.MoveNextRequired();
                            var rhs = Expression.Parse(context);
                            expressions.Add(new AssignmentExpression(slot,
                                TwoSidedExpression.FromCompoundOp(compound.Character, new VariableReference(atPrefixed, context), rhs, context)));
                        }
                        else
                        {
                            context.RestoreCheckpoint(checkpoint);
                            expressions.Add(Expression.Parse(context));
                        }
                    }
                    break;

                // After an aliased element, `name:` is a GOTO label ending the
                // statement, not the next element.
                case UnquotedString { IsLabelDeclaration: true } when !elementExpected:
                    goto ExitWhileTokenLoop;

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
                            var rhs = ParseAliasedProjection(context, scope);
                            expressions.Add(AssignColumnAlias(rhs, aliasCandidate.Value));
                        }
                        else
                        {
                            context.RestoreCheckpoint(checkpoint);
                            expressions.Add(TryParseQualifiedStar(context, scope) ?? Expression.Parse(context));
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
                            var rhs = ParseAliasedProjection(context, scope);
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
                // enclosing subquery / derived table when this query is
                // parenthesized. The pre-expression switch above also has a `)`
                // case for the empty-projection error path; this one fires
                // when at least one expression has been parsed.
                case Operator { Character: ')' }:
                    if (!scope.Parenthesized)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    goto ExitWhileTokenLoop;

                // A WINDOW clause with no FROM before it, read with the
                // ORDER BY after it — never an alias, even where one could
                // stand (probed 2026-09-29 against SQL Server 2025).
                case Name when IsWindowClauseAhead(context):
                    ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy, scope, []);
                    goto ExitWhileTokenLoop;

                // `name:` is a GOTO label ending the statement, never an alias.
                case UnquotedString { IsLabelDeclaration: true }:
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
                    fromClause.ProjectionRefsEnd = context.DeferredNextValueRefs?.Count ?? 0;
                    context.Batch.BindErrors?.EnterClause(context.Token, BindClause.From);
                    List<FromSource> sources;
                    List<JoinSpec> joins;
                    if (preParsedSources is not null)
                    {
                        // Sources came from the pre-pass; resume from the token
                        // after them and consume only the WHERE tail.
                        sources = preParsedSources;
                        joins = preParsedJoins!;
                        context.RestoreCheckpoint(afterSources);
                        ConsumeWhereOrderByWithOuterScope(context, fromClause, [.. sources], [.. joins], allowOrderBy, scope);
                    }
                    else
                    {
                        sources = [];
                        joins = [];
                        using (ParserScope.Enter(ref context.BrowseFlattenFrom, browseFrom))
                            ParseFromSourceAndJoins(context, scope, sources, joins, fromClause, allowOrderBy);
                    }

                    if (topExpression is not null && fromClause.OffsetExpression is not null)
                        throw SimulatedSqlException.TopAndOffsetMutuallyExclusive();
                    RejectSequenceDrawUnderOrderBy(context, fromClause, expressions, sequenceDrawsBefore, unwindowedSequenceDrawsBefore);
                    // A nested query — a derived table or a view's body — whose
                    // TOP is a constant 100 PERCENT keeps every row, so real
                    // drops its ORDER BY and returns the rows in scan order
                    // (probe-confirmed 2026-09-23, WITH TIES and parentheses
                    // alike); at the top level the ORDER BY stands.
                    if (scope.OrderingIgnoredUnderFullTop && topPercent && topExpression is not null
                        && ConstantFolding.TryFold(topExpression, context, out var topConstant)
                        && !topConstant.IsNull && topConstant.CoerceTo(SqlType.Float).AsDouble == 100)
                    {
                        // Ties add nothing to every row.
                        fromClause.OrderBy.Clear();
                        topWithTies = false;
                    }
                    RejectMisplacedIdentityFunction(context, expressions, intoTarget);
                    ValidateForcedSeeks(context, [.. sources], [.. joins], fromClause.Having is { } having ? [.. fromClause.Excluders, having] : fromClause.Excluders, expressions, scope.Position == QueryPosition.Subquery && expressions.Count == 1 ? expressions[0] : null);
                    if (!context.Batch.IsSkipping && ReadsPredicateColumns(sources, fromClause, joins, distinct))
                        Simulation.LoadPredicateStatistics(context.Batch, sources, PredicateOperands(fromClause, joins, distinct ? expressions : null));
                    ApplyShortestPath(context, scope, sources, joins, expressions, fromClause);
                    ExpandStars(context.Batch.CurrentDatabase.Collation, expressions, fromClause.Match?.StarOrder(sources) ?? sources);
                    NameKeyPseudoColumns(expressions, sources);
                    JoinSpec[] joinArray = [.. joins];
                    var plan = BuildSqlProjection(context.Batch, [.. sources], joinArray, expressions, fromClause, distinct, topExpression, topPercent, topWithTies, aggregates, windows, scope, ResolveAssignmentMode(expressions), intoTarget, context.ReadColumnSink);
                    // The decorrelated key plan an enclosing EXISTS / IN can
                    // answer itself from; null for every body that isn't
                    // equi-correlated, which is every top-level query.
                    plan.SemiJoin = TryBuildSemiJoinShape(
                        context.Batch, plan, [.. sources], joinArray, expressions, fromClause,
                        distinct, topExpression, aggregates, windows, scope.OuterTypeResolver);
                    context.Batch.InlinedCalls?.SettleBlock(context.InliningBlock, distinct && fromClause.OrderBy.Count > 0);
                    plan.IntoDataSpace = intoDataSpace;
                    if (fromClause.GroupingSets.Count > 0 && aggregates.Exists(static aggregate => aggregate.ClrArguments is not null))
                        context.GroupsClrAggregate = true;
                    if (sources.Count > 0)
                    {
                        plan.SeekShape = new SeekBodyShape(
                            [.. sources],
                            joinArray,
                            fromClause.Having is { } seekHaving ? [.. fromClause.Excluders, seekHaving] : fromClause.Excluders,
                            SeekReachableProjections(context.Batch.CurrentDatabase.Collation, expressions, windows, topExpression is not null || fromClause.OffsetExpression is not null));
                    }
                    return plan;

                // SELECT projection INTO target [FROM ...] — captures the
                // destination table name. Real SQL Server requires every
                // projection to have a name (Msg 1038) and rejects duplicate
                // names (Msg 2705); both validations happen at build time
                // alongside the schema-inference walk, so we can flag the
                // offending column with the target table name in the message.
                case ReservedKeyword { Keyword: Keyword.Into }:
                    if (intoTarget is not null || DefinesModuleQuery(context, scope))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    intoTarget = BatchContext.ParseObjectName(context);
                    intoDataSpace = ParseOptionalIntoDataSpace(context);
                    continue;

                // WHERE / GROUP BY / HAVING with no FROM clause. All three are
                // legal against the one synthesized row a source-less SELECT
                // reads (`SELECT COUNT(*) HAVING COUNT(*) > 0` → 1 row,
                // `SELECT 1 GROUP BY ()` → 1 row, probe-confirmed), and
                // ConsumeWhereAndOrderBy already reads them in grammar order
                // from whichever of the three the cursor sits on.
                case ReservedKeyword { Keyword: Keyword.Where or Keyword.Group or Keyword.Having }:
                    ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy, scope, []);
                    goto ExitWhileTokenLoop;

                case ReservedKeyword { Keyword: Keyword.Order }:
                    if (allowOrderBy || scope.RefusesTrailingClauses)
                        ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy, scope, []);
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
                // CTE-prefixed follow-up statement, or a view's WITH CHECK
                // OPTION ending the body; raise Msg 319 for the first to mirror
                // SQL Server's specific error here. Checked before the general
                // statement-boundary case (which also treats WITH as a
                // boundary) so the more specific Msg 319 wins.
                case ReservedKeyword { Keyword: Keyword.With } when !scope.Parenthesized && AtWithCheckOption(context):
                    goto ExitWhileTokenLoop;
                case ReservedKeyword { Keyword: Keyword.With } when !scope.Parenthesized:
                    throw SimulatedSqlException.CteRequiresPrecedingSemicolon();

                // In a statement's own query, the start of another statement
                // terminates this SELECT — the dispatch loop picks up there.
                // Inside parentheses these keywords stay invalid (fall through
                // to the generic Msg 102 below). A `(` after an element opens
                // a parenthesized query statement the same way.
                case ReservedKeyword statementStart when !scope.Parenthesized && Simulation.IsStatementBoundary(statementStart):
                    goto ExitWhileTokenLoop;
                case Operator { Character: '(' } when !scope.Parenthesized:
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
        if (fromClause.ProjectionRefsEnd < 0)
            fromClause.ProjectionRefsEnd = context.DeferredNextValueRefs?.Count ?? 0;
        ResolvePendingNamedWindows(context);
        RejectMisplacedIdentityFunction(context, expressions, intoTarget);

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
        RejectSequenceDrawUnderOrderBy(context, fromClause, expressions, sequenceDrawsBefore, unwindowedSequenceDrawsBefore);

        // A SELECT reading no FROM or WHERE that only assigns variables is a
        // SET to real, whose operands inline nothing.
        context.Batch.InlinedCalls?.SettleBlock(
            context.InliningBlock,
            (distinct && fromClause.OrderBy.Count > 0) || (fromClause.Excluders.Count == 0 && ResolveAssignmentMode(expressions)));

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
            return Placed(intoDataSpace, BuildSqlProjection(context.Batch, [], [], expressions, fromClause, distinct,
                topExpression, topPercent, topWithTies, aggregates, windows,
                scope.WithOuter(context.OuterTypeResolver ?? scope.OuterTypeResolver), ResolveAssignmentMode(expressions),
                intoTarget, context.ReadColumnSink));
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

        return Placed(intoDataSpace, BuildSynthesizedSqlRow(context.Batch, expressions, fromClause.Excluders, fromClause.OrderBy,
            topPercent
                ? (topExpression is not null && ResolveTopPercentValue(topExpression, context.Batch) > 0 ? 1 : 0)
                : ResolveRowCountLimit(topExpression, RowLimitKind.Top, context.Batch),
            ResolveRowCountLimit(fromClause.OffsetExpression, RowLimitKind.Offset, context.Batch),
            ResolveRowCountLimit(fromClause.FetchExpression, RowLimitKind.Fetch, context.Batch),
            ResolveAssignmentMode(expressions), intoTarget, scope.WithOuter(context.OuterTypeResolver ?? scope.OuterTypeResolver),
            containsSubquery));
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
    private static void RejectSequenceDrawUnderOrderBy(ParserContext context, FromClause fromClause, List<Expression> projection, int sequenceDrawsBefore, int unwindowedSequenceDrawsBefore)
    {
        SettleDeferredNextValueRefs(context, fromClause.OrderBy.Count > 0, OrderByNamesOnlyConstants(fromClause, projection), fromClause.OffsetExpression is not null, fromClause.ProjectionRefsStart, fromClause.ProjectionRefsEnd);
        if (fromClause.OrderBy.Count > 0 && context.UnwindowedSequenceDrawsParsed > unwindowedSequenceDrawsBefore)
            throw SimulatedSqlException.NextValueForNotAllowedWithOrderBy();
        // An OFFSET can only follow an ORDER BY, so this is reached only for a
        // draw the OVER exemption carried past the check above — real refuses
        // that one too, since the OVER lifts the ORDER BY refusal alone.
        if (fromClause.OffsetExpression is not null && context.SequenceDrawsParsed > sequenceDrawsBefore)
            throw SimulatedSqlException.NextValueForNotAllowedWithRowLimit();
        if (context.SequenceOverMismatch)
        {
            context.SequenceOverMismatch = false;
            throw SimulatedSqlException.NextValueForOverMismatch();
        }
    }

    /// <summary>
    /// Whether every <c>ORDER BY</c> key names a select-list item that is a
    /// written constant, by ordinal or by its alias — the statement's sort is
    /// then one real removes before it judges a <c>TOP</c> count's
    /// <c>NEXT VALUE FOR</c> (probed 2026-09-29 against SQL Server 2025: a
    /// column, a variable, <c>GETDATE()</c>, <c>UPPER('a')</c> or a subquery
    /// item keeps the ordered refusal). A star in the list leaves ordinals
    /// unknowable, so it answers false.
    /// </summary>
    private static Expression UnwrapParentheses(Expression expression) =>
        expression is Parenthesized { Wrapped: var inner } ? UnwrapParentheses(inner) : expression;

    private static bool OrderByNamesOnlyConstants(FromClause fromClause, List<Expression> projection)
    {
        if (fromClause.OrderBy.Count == 0 || projection.Exists(static item => item is StarProjection))
            return false;
        foreach (var key in fromClause.OrderBy)
        {
            Expression? item = null;
            if (key.IsOrdinal)
            {
                if (key.Ordinal >= 1 && key.Ordinal <= projection.Count)
                    item = projection[key.Ordinal - 1];
            }
            else if (key is { MayNameAlias: true, Expr: { } written } && UnwrapParentheses(written) is Reference { ReferencedName: { ImmediateQualifier: null } name })
            {
                item = projection.Find(candidate => candidate is NamedExpression named && Collation.Baseline.Equals(named.Name, name.Leaf));
            }
            if ((item is NamedExpression alias ? alias.Inner : item) is not { IsWrittenConstant: true })
                return false;
        }
        return true;
    }

    /// <summary>
    /// Settles the references this query spec parsed under a restriction the
    /// statement-level refusals outrank. Each reference takes the
    /// highest-precedence refusal that applies to it — Msg 11723 for an
    /// <c>ORDER BY</c> unless it names its own <c>OVER</c>, its clause's own
    /// refusal, Msg 11739 for an <c>OFFSET</c> — and the first reference in
    /// parse order that has one raises it (probed 2026-09-29 against SQL Server
    /// 2025: a <c>WHERE</c>, <c>GROUP BY</c>, <c>HAVING</c>, <c>ON</c>, <c>TOP</c>
    /// or conditional-arm reference in an ordered statement is Msg 11723, and a
    /// windowed one under <c>TOP</c> stays Msg 11739). A branch a set operator
    /// follows is left to the chain, whose Msg 11721 outranks all of these.
    /// </summary>
    private static void SettleDeferredNextValueRefs(ParserContext context, bool orderBy, bool constantOrderBy, bool offset, int projectionStart, int projectionEnd)
    {
        if (context.DeferredNextValueRefs is not { } refs)
            return;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect })
            return;
        context.DeferredNextValueRefs = null;
        // Real binds the select list after the other clauses, so the
        // references it parsed are judged last (probed 2026-09-29: a windowed
        // one under TOP in the select list, with an unwindowed one in the
        // WHERE, is the WHERE's Msg 11723).
        // A TOP count's references are judged after the select list's
        // (probed 2026-09-29: a windowed select-list reference beside one in
        // the TOP is Msg 11739, the TOP's own being left for last).
        for (var pass = 0; pass < 3; pass++)
        {
            for (var i = 0; i < refs.Count; i++)
            {
                var isTop = refs[i].InTop;
                if (isTop ? pass != 2 : (i >= projectionStart && i < projectionEnd) != (pass == 1))
                    continue;
                var reference = refs[i];
                var refusal = NextValueForScope.Allowed;
                // An OVER body's reference is one of the clauses Msg 11720
                // names, and the ORDER BY refusal doesn't reach it.
                // An ORDER BY whose keys all name constant select items is one
                // real drops from the statement, which a TOP count's reference
                // then never sees (probed 2026-09-29: Msg 11720 rather than 11723).
                if (orderBy && !reference.Windowed && !reference.OverBody && !(reference.InTop && constantOrderBy))
                    refusal = NextValueForScope.OrderedStatement;
                if (reference.Scope != NextValueForScope.Allowed && (refusal == NextValueForScope.Allowed || reference.Scope < refusal))
                    refusal = reference.Scope;
                if (offset && (refusal == NextValueForScope.Allowed || NextValueForScope.RowLimited < refusal))
                    refusal = NextValueForScope.RowLimited;
                if (refusal != NextValueForScope.Allowed)
                    throw Expressions.NextValueFor.RefusalFor(refusal);
            }
        }
    }

    /// <summary>
    /// Wraps a projection expression in its column alias, mirroring SQL
    /// Server's rejection of an empty alias ("" / [] / '' / N'') with Msg
    /// 1038. Shared by every select-list alias site: the AS form, the bare
    /// postfix form, and the alias-on-left <c>alias = expr</c> form.
    /// </summary>
    private static NamedExpression AssignColumnAlias(Expression expression, string alias)
    {
        if (alias.Length == 0)
            throw SimulatedSqlException.EmptyColumnAlias();
        // The seed and increment errors name the column.
        if (expression is IdentityFunction identity)
            identity.Resolve(alias);
        return new NamedExpression(expression, alias);
    }

    /// <summary>
    /// Parses the value of the alias-on-left <c>alias = expr</c> form, which
    /// may be <c>SELECT … INTO</c>'s <c>IDENTITY()</c> function.
    /// </summary>
    private static Expression ParseAliasedProjection(ParserContext context, QueryScope scope) =>
        context.Token is ReservedKeyword { Keyword: Keyword.Identity } && scope.AcceptsIdentityFunction
            ? new IdentityFunction(context)
            : Expression.Parse(context);

    /// <summary>
    /// Reads the <c>ON</c> clause a <c>SELECT … INTO</c> target may carry: a
    /// filegroup by name or as a string, or a partition scheme, which only the
    /// statement's run refuses (<see cref="Simulation.ResolveIntoDataSpace"/>).
    /// </summary>
    private static DataSpaceClause? ParseOptionalIntoDataSpace(ParserContext context)
    {
        // The cursor is on the target's last token and stays on the clause's
        // last one, as the select-list loop expects.
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is not ReservedKeyword { Keyword: Keyword.On })
        {
            context.RestoreCheckpoint(checkpoint);
            return null;
        }
        switch (context.GetNextRequired())
        {
            case Literal { Value.Type: VarcharSqlType or NVarcharSqlType } quoted:
                return new DataSpaceClause(quoted.Value.AsString, columns: null);
            case Name name:
                var afterName = context.SaveCheckpoint();
                if (context.GetNextOptional() is not Operator { Character: '(' })
                {
                    context.RestoreCheckpoint(afterName);
                    return new DataSpaceClause(name.Value, columns: null);
                }
                List<string> columns = [];
                do
                {
                    if (context.GetNextRequired() is not Name column)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    columns.Add(column.Value);
                    context.MoveNextRequired();
                } while (context.Token is Operator { Character: ',' });
                return context.Token is Operator { Character: ')' }
                    ? new DataSpaceClause(name.Value, columns)
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>Attaches a <c>SELECT … INTO</c>'s <c>ON</c> clause to its finished plan.</summary>
    private static Selection Placed(DataSpaceClause? intoDataSpace, Selection plan)
    {
        plan.IntoDataSpace = intoDataSpace;
        return plan;
    }

    /// <summary>
    /// Refuses <c>SELECT … INTO</c>'s <c>IDENTITY()</c> function in a query
    /// without <c>INTO</c> (Msg 177) or one a set operator follows (Msg 1057).
    /// Called once the query's own clauses are read, so the cursor is on
    /// whatever follows them.
    /// </summary>
    private static void RejectMisplacedIdentityFunction(ParserContext context, List<Expression> expressions, MultiPartName? intoTarget)
    {
        if (!expressions.Exists(expression => expression is NamedExpression { Inner: IdentityFunction }))
            return;
        if (intoTarget is null)
            throw SimulatedSqlException.IdentityFunctionWithoutInto();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Union or Keyword.Intersect or Keyword.Except })
            throw SimulatedSqlException.IdentityFunctionWithSetOperator();
    }

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
        JoinSpec[] joins,
        bool allowOrderBy,
        QueryScope scope)
    {
        SqlType MyResolver(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, scope.OuterTypeResolver);

        using var outer = ParserScope.Enter(ref context.OuterTypeResolver, MyResolver);
        // A WHERE-clause CONTAINS / FREETEXT binds its column specification
        // against these same sources, as does a MATCH, which reads the joins too.
        using var scopeSources = ParserScope.Enter(ref context.ScopeSources, sources);
        using var scopeJoins = ParserScope.Enter(ref context.ScopeJoins, joins);
        ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy, scope, sources);
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
    private static void ConsumeWhereAndOrderBy(ParserContext context, FromClause fromClause, bool allowOrderBy, QueryScope scope, FromSource[] localSources)
    {
        using var clause = ParserScope.Enter(ref context.InliningClause, InliningClause.Other);
        // A parenthesized INSERT source's own query may not carry an ORDER BY
        // (Msg 156 on the keyword); a derived table or subquery nested inside
        // it parses at its own position and keeps the ordinary rules.
        if ((scope.RefusesTrailingClauses || RefusesParenthesizedOrderBy(context, scope))
            && context.Token is ReservedKeyword { Keyword: Keyword.Order } orderKeyword)
        {
            throw SimulatedSqlException.SyntaxErrorNearKeyword(orderKeyword);
        }

        // WHERE / GROUP BY / HAVING reject windowed functions (Msg 4108) and
        // NEXT VALUE FOR (Msg 11720). Toggle the parser-context flags for the
        // duration of those parses; ORDER BY (which DOES allow windows but
        // rejects NEXT VALUE FOR) handled separately below.
        using (ParserScope.Enter(ref context.AllowsWindowExpressions, false))
        using (context.EnterNextValueForScope(NextValueForScope.Clause))
        {
            var collector = context.AggregateCollector;
            var aggregatesBefore = collector?.Count ?? 0;
            using (ParserScope.Save(ref context.MatchScope))
            using (ParserScope.Enter(ref context.InWhereClause, true))
            {
                while (context.Token is ReservedKeyword { Keyword: Keyword.Where })
                {
                    context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Where);
                    // A MATCH predicate binds only here, against this scope.
                    context.MatchScope = new Expressions.MatchScope();
                    fromClause.Excluders.Add(BooleanExpression.SimplifyForFilter(
                        BooleanExpression.Parse(context.MoveNextRequiredReturnSelf()), context));
                    if (context.MatchScope.Edges.Count > 0 || context.MatchScope.ShortestPath is not null)
                        fromClause.Match = context.MatchScope;
                }
            }
            if (collector is not null && collector.Count > aggregatesBefore)
                fromClause.WhereAggregates = collector.GetRange(aggregatesBefore, collector.Count - aggregatesBefore);

            if (context.Token is ReservedKeyword { Keyword: Keyword.Group })
            {
                context.Batch.BindErrors?.EnterClause(context.Token, BindClause.GroupBy);
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.RecursiveBranchConstructs.GroupingOrAggregate = true;
                // `GROUP BY ALL` — ParseGroupByList steps from the ALL onto the
                // first item as it would from the BY.
                var beforeAll = context.SaveCheckpoint();
                fromClause.GroupByAll = context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.All };
                if (!fromClause.GroupByAll)
                    context.RestoreCheckpoint(beforeAll);
                else if (context.SchemaBoundBody != SchemaBoundBody.None)
                    throw SimulatedSqlException.SyntaxNotAllowedInSchemaBoundObject("ALL", 8);
                ParseGroupByList(context, fromClause, localSources, scope.OuterTypeResolver);
                if (fromClause.GroupByAll && fromClause.Excluders.Count > 0)
                {
                    fromClause.GroupByAllFilter = [.. fromClause.Excluders];
                    fromClause.Excluders.Clear();
                    if (fromClause.ReadsRemoteSource)
                        context.PendingBindError ??= SimulatedSqlException.GroupByAllOverRemoteSource();
                }
            }

            if (context.Token is ReservedKeyword { Keyword: Keyword.Having })
            {
                context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Having);
                context.RecursiveBranchConstructs.GroupingOrAggregate = true;
                // A HAVING settles a comparison against a folded-NULL constant
                // the way a WHERE doesn't — see SettleFoldedNullComparisons.
                fromClause.Having = BooleanExpression.SimplifyForFilter(
                    BooleanExpression.Parse(context.MoveNextRequiredReturnSelf()).SettleFoldedNullComparisons(context), context);
            }
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
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.OrderBy);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            // ORDER BY rejects NEXT VALUE FOR (Msg 11720), but allows windowed
            // functions. Toggle just the sequence flag for the duration.
            using (context.EnterNextValueForScope(NextValueForScope.Clause))
            {
                ParseOrderByItems(context, fromClause.OrderBy);
            }
            ConsumeOffsetFetch(context, fromClause, scope);
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
        var (pendingStart, definitionStart) = context.NamedWindowScope;
        var pending = context.PendingNamedWindows;
        for (var i = pendingStart; i < pending.Count; i++)
            pending[i].Window.ApplyNamedWindow(MergeWindowReference(context, pending[i].Reference, []));
        if (pending.Count > pendingStart)
            pending.RemoveRange(pendingStart, pending.Count - pendingStart);
        var definitions = context.NamedWindowDefinitions;
        if (definitions.Count > definitionStart)
            definitions.RemoveRange(definitionStart, definitions.Count - definitionStart);
    }

    /// <summary>
    /// Looks a <c>WINDOW</c>-clause definition up by name under the database
    /// collation — window names are identifiers, so a case-insensitive
    /// collation resolves <c>OVER W</c> against <c>WINDOW w AS (…)</c>.
    /// </summary>
    private static bool TryFindWindowDefinition(ParserContext context, string name, out Expressions.WindowExpression.WindowBody body)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        var definitions = context.NamedWindowDefinitions;
        for (var i = context.NamedWindowScope.Definitions; i < definitions.Count; i++)
        {
            if (collation.Equals(definitions[i].Name, name))
            {
                body = definitions[i].Body;
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
        // Real's state tells the three misses apart: a block with no WINDOW
        // clause at all, an OVER naming none of the clause's windows, and a
        // definition naming one — itself included, since real doesn't put a
        // name in its own scope (probed 2026-09-29 against SQL Server 2025).
        if (!TryFindWindowDefinition(context, name, out var definition))
        {
            throw SimulatedSqlException.WindowIsUndefined(name, (byte)(visiting.Count > 0
                ? 7
                : context.NamedWindowDefinitions.Count > context.NamedWindowScope.Definitions ? 4 : 3));
        }
        if (collation.Equals(definition.BaseWindowName, name))
            throw SimulatedSqlException.WindowIsUndefined(name, 7);

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
    /// Whether every column a GROUP BY item names binds to an enclosing query
    /// rather than to <paramref name="localSources"/>: Msg 164's "outer
    /// reference" — the enclosing query's columns at any depth, a trigger's
    /// <c>inserted</c>, an <c>UPDATE</c>'s target alike (probed 2026-10-07
    /// against SQL Server 2025). A name binding nowhere answers false, leaving
    /// it to its own Msg 207, which real reports instead; so does a placeholder
    /// source, whose missing table defers the statement's binding.
    /// </summary>
    private static bool NamesOnlyOuterColumns(List<Expression[]> item, FromSource[] localSources, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        if (outerTypeResolver is null || AnyPlaceholderSource(localSources))
            return false;
        // The walk reaches every column the item names and none inside a
        // nested subquery, which binds in its own scope (and is Msg 144 here
        // anyway).
        var namesOnlyOuter = true;
        foreach (var fragment in item)
        {
            foreach (var expression in fragment)
            {
                expression.Walk((_, shape) =>
                {
                    if (namesOnlyOuter && shape.Column is { } column)
                        namesOnlyOuter = BindsOnlyOutside(column, localSources, outerTypeResolver);
                    return namesOnlyOuter;
                });
            }
        }
        return namesOnlyOuter;
    }

    private static bool BindsOnlyOutside(MultiPartName column, FromSource[] localSources, Func<MultiPartName, SqlType> outerTypeResolver)
    {
        if (FindSourceColumnOfAnyKind(localSources, column).SourceIndex >= 0
            || (column.ImmediateQualifier is not null && QualifiesAnySource(localSources, column)))
        {
            return false;
        }
        try
        {
            _ = outerTypeResolver(column);
            return true;
        }
        catch (SimulatedSqlException)
        {
            return false;
        }
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
    private static void ParseGroupByList(ParserContext context, FromClause fromClause, FromSource[] localSources, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        using var clause = ParserScope.Enter(ref context.InliningClause, InliningClause.GroupBy);
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

            fromClause.GroupingSetsWritten |= context.Token is UnquotedString
            {
                ContextualKeyword: ContextualKeyword.Rollup or ContextualKeyword.Cube or ContextualKeyword.Grouping,
            };
            var contribution = ParseGroupByItem(context);
            itemContributions.Add(contribution);

            // Both messages are held rather than thrown: real parses the whole
            // statement before binding it, so a stray token after the clause
            // outranks them (see ParserContext.PendingBindError). The
            // ??= keeps the first offending item's message, which is what an
            // immediate throw produced.
            if (context.AggregatesParsed > aggregatesBefore || context.SubqueriesParsed > subqueriesBefore)
                context.PendingBindError ??= SimulatedSqlException.AggregateOrSubqueryInGroupBy();

            // The empty grouping set contributes no expression at all, so there
            // is nothing for Msg 164 to require a column of. Probe-confirmed
            // legal on real 2026-07-24: `GROUP BY ()`, `GROUPING SETS (())`,
            // `GROUPING SETS ((a),())` and `GROUP BY (), a` all return rows.
            var contributesAnExpression = false;
            foreach (var fragment in contribution)
                contributesAnExpression |= fragment.Length > 0;

            if (contributesAnExpression
                && (context.ColumnReferencesParsed == columnsBefore || NamesOnlyOuterColumns(contribution, localSources, outerTypeResolver)))
            {
                context.PendingBindError ??= SimulatedSqlException.GroupByExpressionHasNoLocalColumn();
            }
        } while (context.Token is Operator { Character: ',' });

        // GROUP BY ALL takes plain items only: a ROLLUP / CUBE / GROUPING SETS
        // item, or an empty set beside another item, is Msg 1028 at the token
        // after the list — the legacy WITH form below raises it at its ROLLUP
        // / CUBE word (probed 2026-09-30 against SQL Server 2025).
        if (fromClause.GroupByAll
            && (fromClause.GroupingSetsWritten
                || (itemContributions.Count > 1 && itemContributions.Exists(static item => item is [[]]))))
        {
            throw SimulatedSqlException.GroupingSetsInGroupByAll();
        }

        // Cartesian product of per-item contributions: each combination of
        // one fragment from each item gets concatenated into one grouping
        // set. Order matters only insofar as result-row ordering follows
        // grouping-set iteration order.
        var combined = new List<List<Expression>> { new() };
        var setCount = 1L;
        foreach (var item in itemContributions)
            setCount = Math.Min(setCount * item.Count, MaxGroupingSets + 1);
        if (setCount > MaxGroupingSets)
        {
            context.PendingBindError ??= SimulatedSqlException.TooManyGroupingSets();
            for (var i = 0; i < itemContributions.Count; i++)
                itemContributions[i] = [itemContributions[i][0]];
        }
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
        if (context.Token is ReservedKeyword { Keyword: Keyword.With } && !AtWithCheckOption(context))
        {
            var modifierToken = context.GetNextRequired();
            if (fromClause.GroupByAll && modifierToken is UnquotedString { ContextualKeyword: ContextualKeyword.Rollup or ContextualKeyword.Cube })
                throw SimulatedSqlException.GroupingSetsInGroupByAll();
            fromClause.GroupingSetsWritten = true;
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
    /// The most grouping sets one GROUP BY may expand to; past it real
    /// raises Msg 10703 (see <see cref="SimulatedSqlException.TooManyGroupingSets"/>).
    /// </summary>
    private const int MaxGroupingSets = 4096;

    /// <summary>
    /// Parses one top-level GROUP BY item. Returns the list of fragments
    /// (each fragment a column-list) the item contributes. A plain expression
    /// contributes a single one-element fragment <c>[[expr]]</c>; a ROLLUP
    /// contributes <c>N+1</c> fragments shrinking from full prefix to empty;
    /// a CUBE contributes all <c>2^N</c> subsets; a GROUPING SETS contributes
    /// each member's sets in turn — a member being a ROLLUP or CUBE (all of its
    /// sets), a parenthesized composite whose elements, ROLLUP and CUBE
    /// included, Cartesian-combine (<c>GROUPING SETS ((g, ROLLUP(h)))</c> is
    /// <c>(g, h), (g)</c>), the empty <c>()</c>, or a bare expression (probed
    /// 2026-10-01 against SQL Server 2025). A GROUPING SETS inside another is
    /// Msg 102 near <c>sets</c>.
    /// </summary>
    private static List<Expression[]> ParseGroupByItem(ParserContext context)
    {
        if (context.Token is UnquotedString { ContextualKeyword: var kw }
            && kw is ContextualKeyword.Rollup or ContextualKeyword.Cube or ContextualKeyword.Grouping)
        {
            if (kw != ContextualKeyword.Grouping)
                return ParseRollupOrCube(context, cube: kw == ContextualKeyword.Cube);

            if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Sets })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var fragments = new List<Expression[]>();
            do
            {
                context.MoveNextRequired();
                var member = ParseGroupingSetMember(context);
                if (fragments.Count + member.Count > MaxGroupingSets)
                    context.PendingBindError ??= SimulatedSqlException.TooManyGroupingSets();
                else
                    fragments.AddRange(member);
            }
            while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return fragments;
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
    /// Parses <c>ROLLUP(…)</c> / <c>CUBE(…)</c>, entered on the keyword. Each
    /// element is an expression or a parenthesized composite <c>(a, b)</c>
    /// that rolls up as one unit — <c>ROLLUP((g, h), s)</c> is
    /// <c>(g, h, s), (g, h), ()</c>; an empty <c>()</c> element is Msg 102
    /// near its <c>)</c> (probed 2026-10-01 against SQL Server 2025). Past
    /// <see cref="MaxGroupingSets"/> the expansion is refused with a held
    /// Msg 10703 rather than built.
    /// </summary>
    private static List<Expression[]> ParseRollupOrCube(ParserContext context, bool cube)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var units = new List<Expression[]>();
        do
        {
            context.MoveNextRequired();
            units.Add(ParseGroupingUnit(context));
        }
        while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        if (cube && units.Count > 12)
        {
            context.PendingBindError ??= SimulatedSqlException.TooManyGroupingSets();
            return [[.. units.SelectMany(static unit => unit)]];
        }

        if (!cube)
        {
            var rollup = new List<Expression[]>(units.Count + 1);
            for (var k = units.Count; k >= 0; k--)
                rollup.Add([.. units.Take(k).SelectMany(static unit => unit)]);
            return rollup;
        }

        var count = 1 << units.Count;
        var fragments = new List<Expression[]>(count);
        for (var mask = count - 1; mask >= 0; mask--)
        {
            var fragment = new List<Expression>();
            for (var b = 0; b < units.Count; b++)
            {
                if ((mask & (1 << b)) != 0)
                    fragment.AddRange(units[b]);
            }
            fragments.Add([.. fragment]);
        }
        return fragments;
    }

    /// <summary>
    /// One ROLLUP / CUBE element: a parenthesized composite of two or more
    /// expressions, or an expression — a lone parenthesized one included,
    /// which reads on as an expression (<c>(g) + 1</c>).
    /// </summary>
    private static Expression[] ParseGroupingUnit(ParserContext context)
    {
        if (context.Token is Operator { Character: '(' })
        {
            var checkpoint = context.SaveCheckpoint();
            if (context.GetNextRequired() is Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var first = Expression.Parse(context);
            if (context.Token is Operator { Character: ',' })
            {
                var list = new List<Expression> { first };
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
            context.RestoreCheckpoint(checkpoint);
        }
        return [Expression.Parse(context)];
    }

    private static bool NextIsOpenParenthesis(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return next is Operator { Character: '(' };
    }

    /// <summary>
    /// Parses one member of a <c>GROUPING SETS(...)</c> list into the sets it
    /// contributes: a ROLLUP or CUBE gives all of its sets, the empty
    /// <c>()</c> the grand total, a parenthesized composite the Cartesian
    /// combination of its elements (each an expression, a ROLLUP or a CUBE),
    /// and a bare expression one set of itself. A parenthesized expression
    /// that reads on past its <c>)</c> (<c>(a + 1) * 2</c>) is a bare
    /// expression.
    /// </summary>
    private static List<Expression[]> ParseGroupingSetMember(ParserContext context)
    {
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Rollup or ContextualKeyword.Cube } keyword
            && NextIsOpenParenthesis(context))
        {
            return ParseRollupOrCube(context, cube: keyword.ContextualKeyword == ContextualKeyword.Cube);
        }

        if (context.Token is Operator { Character: '(' })
        {
            var checkpoint = context.SaveCheckpoint();
            if (context.GetNextRequired() is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                return [[]];
            }
            List<List<Expression>> combined = [[]];
            while (true)
            {
                var element = context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Rollup or ContextualKeyword.Cube } nested
                    && NextIsOpenParenthesis(context)
                    ? ParseRollupOrCube(context, cube: nested.ContextualKeyword == ContextualKeyword.Cube)
                    : [[Expression.Parse(context)]];
                if ((long)combined.Count * element.Count > MaxGroupingSets)
                {
                    context.PendingBindError ??= SimulatedSqlException.TooManyGroupingSets();
                    element = [element[0]];
                }
                var next = new List<List<Expression>>(combined.Count * element.Count);
                foreach (var prefix in combined)
                {
                    foreach (var fragment in element)
                        next.Add([.. prefix, .. fragment]);
                }
                combined = next;
                if (context.Token is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            if (context.Token is null or Operator { Character: ',' or ')' })
                return [.. combined.Select(static set => set.ToArray())];
            context.RestoreCheckpoint(checkpoint);
        }
        return [[Expression.Parse(context)]];
    }

    /// <summary>
    /// Raises Msg 4115 for an aggregate a row-count operand's subquery moved
    /// to this query because it reads only this query's columns — the count
    /// reads the column as surely as a bare reference would (probed 2026-09-28
    /// against SQL Server 2025: <c>OFFSET (SELECT MAX(t.a) FROM u) ROWS</c>).
    /// </summary>
    private static void RefuseRowLimitAggregates(ParserContext context, int aggregatesBefore)
    {
        if (context.AggregateCollector is not { } collector || collector.Count <= aggregatesBefore)
            return;
        MultiPartName? first = null;
        collector[aggregatesBefore].Operand?.VisitColumnReferences(name => first ??= name);
        if (first is { } column)
            throw SimulatedSqlException.ColumnReferenceNotAllowed(column);
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
    private static void ConsumeOffsetFetch(ParserContext context, FromClause fromClause, QueryScope scope)
    {
        // A count may read an enclosing query's columns, as TOP's may; this
        // query's own columns stay refused (Msg 4115), so the check resolves
        // through the enclosing scope alone rather than the ORDER BY's.
        void CheckCount(Expression expression, RowLimitKind kind)
        {
            using var enclosing = ParserScope.Enter(ref context.OuterTypeResolver, scope.OuterTypeResolver);
            if (!ReadsOuterColumns(expression, percent: false, context, scope.OuterTypeResolver, kind))
                _ = ResolveRowCountLimit(expression, kind, context.Batch);
        }

        // FETCH at this position with no preceding OFFSET → Msg 153.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Fetch })
            throw SimulatedSqlException.FetchInvalidUsageWithoutOffset();

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Offset })
            return;

        context.MoveNextRequired();
        var aggregatesBefore = context.AggregateCollector?.Count ?? 0;
        var offsetExpression = Expression.Parse(context);
        RefuseRowLimitAggregates(context, aggregatesBefore);
        CheckCount(offsetExpression, RowLimitKind.Offset);
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

        aggregatesBefore = context.AggregateCollector?.Count ?? 0;
        var fetchExpression = Expression.Parse(context);
        RefuseRowLimitAggregates(context, aggregatesBefore);
        CheckCount(fetchExpression, RowLimitKind.Fetch);
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
        // A reference in an ORDER BY item is refused where it parses.
        using var deferral = ParserScope.Enter(ref context.DeferNextValueRefusals, false);
        using var clause = ParserScope.Enter(ref context.InliningClause, InliningClause.OrderBy);
        ParseOrderByItemsCore(context, orderBy);
    }

    private static void ParseOrderByItemsCore(ParserContext context, List<OrderBySpec> orderBy)
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
            // before the term is added. A constant whose fold raises is no
            // constant to real: it stands as a sort key, and the error
            // surfaces when the plan starts (probed 2026-09-26 — `ORDER BY
            // 1/0` is Msg 8134 even over an empty table, `ORDER BY 1/1` Msg
            // 408).
            if (expr.IsWrittenConstant && !ConstantFolding.FoldRaises(expr, context))
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

    /// <summary>
    /// The ordinal an ORDER BY term names when it is an integer literal, or
    /// null. Parentheses and unary minus are peeled — real's grammar takes a
    /// signed integer constant here — so <c>(1)</c> is ordinal 1 and <c>-1</c>
    /// is ordinal -1 (out of range, Msg 108).
    /// </summary>
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
    private static Selection BuildSynthesizedSqlRow(BatchContext parseBatch, List<Expression> expressions, List<BooleanExpression> excluders, List<OrderBySpec> orderBy, int? topCount, int? offsetCount, int? fetchCount, bool isAssignmentOnly, MultiPartName? intoTarget, QueryScope scope, bool containsSubquery)
    {
        // The FROM-less SELECT path bakes projection values at parse time
        // (see the Run-then-GetSqlType loop below) — replaying that closure
        // across invocations would emit the same stale NEWID / GETDATE /
        // @@TRANCOUNT / NEXT VALUE FOR result every call. Disqualify the
        // batch from plan-cache promotion.
        parseBatch.HasSessionScopedReference = true;

        // A star with no FROM clause has nothing to expand: real refuses a
        // bare one (Msg 263) and a qualified one (Msg 107), except that an
        // EXISTS body's bare star is never read (probed 2026-09-24).
        for (var i = 0; i < expressions.Count; i++)
        {
            if (expressions[i] is StarProjection star)
            {
                expressions[i] = star.Qualifier is null && scope.Position == QueryPosition.Exists
                    ? new Value(SqlValue.FromInt32(1))
                    : throw star.Unexpandable();
            }
        }

        // Msg 108 while compiling, as on the FROM path.
        foreach (var spec in orderBy)
        {
            if (spec.IsOrdinal && (spec.Ordinal < 1 || spec.Ordinal > expressions.Count))
                throw SimulatedSqlException.OrderByPositionOutOfRange(spec.Ordinal);
        }

        var values = new SqlValue[expressions.Count];
        var schema = new SqlType[expressions.Count];
        var columnNames = new string[expressions.Count];

        // GetSqlType-then-Run, the order real compiles and executes in: a
        // type-pair refusal (`1/0 + CAST(NULL AS date)`) is the compile error
        // real reports ahead of any runtime one, and a CASE / COALESCE caches
        // its unified type there, which is what makes the arm Run picks
        // convert to it (`CASE … int … ELSE varchar END` is int, not the
        // taken arm's varchar).
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
        // rows for anyone to read. SET FMTONLY ON is the same — it describes
        // `SELECT CAST('a' AS int)` where running it raises Msg 245.
        var referencesOuterColumns = containsSubquery || parseBatch.IsSkipping || parseBatch.Connection.FmtOnly;
        foreach (var expression in expressions)
            expression.VisitColumnReferences(_ => referencesOuterColumns = true);

        // A FROM-less SELECT holds no sources, so every name it can't hand to an
        // enclosing scope is unbindable — which makes a *qualified* one Msg 4104
        // and an unqualified one Msg 207, the split UnresolvedNameError carries.
        // This is the path a derived table over no FROM takes, so it is what
        // reports a body that names a sibling FROM source.
        SqlType TypeResolver(MultiPartName column) =>
            scope.OuterTypeResolver is not null
                ? scope.OuterTypeResolver(column)
                : throw UnresolvedNameError([], column);

        // An enclosing query's .nodes() column is read only through a method.
        foreach (var expression in expressions)
            RejectDirectNodesColumnRead(expression, [], parseBatch.Parser.EnclosingScopes);
        foreach (var excluder in excluders)
            RejectDirectNodesColumnRead(excluder, [], parseBatch.Parser.EnclosingScopes);

        var parseRuntime = new RuntimeContext(column => throw SimulatedSqlException.InvalidColumnName(column), parseBatch);
        // The WHERE binds too, so a comparison real refuses while compiling —
        // `WHERE CAST(NULL AS int) = CAST(NULL AS date)`, whose NULL operand
        // folded it to UNKNOWN — still raises.
        foreach (var excluder in excluders)
            excluder.Bind(parseBatch, TypeResolver);

        for (var i = 0; i < expressions.Count; i++)
        {
            columnNames[i] = expressions[i].Name;
            schema[i] = expressions[i].GetSqlType(parseBatch, TypeResolver);
        }

        // A statement's own select list names its output collation here as it
        // does over a FROM: a subquery over conflicting columns hands its
        // unresolved collation up to this projection to settle or refuse.
        var feedsAssignment = isAssignmentOnly || scope.FeedsInsert;
        for (var i = 0; i < schema.Length; i++)
        {
            if (UnresolvedCollation.On(schema[i]) is null)
                continue;
            if (feedsAssignment)
                UnresolvedCollation.RequireAssignable(schema[i]);
            else if (scope.NamesOutputCollation)
                RequireSettledOutputCollation(schema[i], "SELECT", i + 1);
        }

        // Settled ahead of the bake below, whose SELECT @v = … assignments
        // read the masks it sets.
        var columnMasks = DataMask.OfProjection(parseBatch, expressions, parseBatch.Parser.OuterMaskResolver ?? (static _ => null), expression => expression.GetSqlType(parseBatch, TypeResolver));

        // Values come from the executor instead when an outer reference is in
        // play; only the types are needed here, and Run would throw on it. An
        // EXISTS never reads its select list, so real evaluates none of it —
        // `EXISTS (SELECT 1/0)` is true (probe-confirmed 2026-09-23) — and the
        // row it counts carries typed NULLs instead.
        //
        // A value that raises is a runtime error on real, sent after the
        // column metadata (probed 2026-09-25: `SELECT 1; SELECT 10/0` returns
        // the 1 first), so the bake keeps the error for the plan to raise when
        // it runs — past its row limit and WHERE, which may mean never.
        ExceptionDispatchInfo? bakeFailure = null;
        for (var i = 0; (scope.ProjectionUnread || !referencesOuterColumns) && i < expressions.Count; i++)
        {
            if (scope.ProjectionUnread)
            {
                values[i] = SqlValue.Null(schema[i]);
                continue;
            }
            try
            {
                var raw = expressions[i].Run(parseRuntime);
                values[i] = raw.IsNull || raw.Type == schema[i] ? raw : raw.CoerceTo(schema[i]);
            }
            catch (SimulatedSqlException error)
            {
                bakeFailure = ExceptionDispatchInfo.Capture(error);
                break;
            }
        }

        var isBareConstantRow = !containsSubquery && topCount is null && offsetCount is null && fetchCount is null
            && excluders.TrueForAll(excluder => ConstantFolding.TryFoldPredicate(excluder, parseBatch.Parser, out var folded) && folded == true);
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

            try
            {
                foreach (var excluder in excluders)
                {
                    if (excluder.Run(new RuntimeContext(Resolve, batch)) != true)
                        return [];
                }
            }
            catch (SimulatedSqlException filterError)
            {
                filterError.RaisedInRowProjection = false;
                throw;
            }

            if (bakeFailure is not null)
            {
                // The row's value is computed past an INSERT's identity draw.
                ((SimulatedSqlException)bakeFailure.SourceException).RaisedInRowProjection = true;
                bakeFailure.Throw();
            }
            if (scope.ProjectionUnread || !referencesOuterColumns)
                return [RowEncoder.EncodeRow(schema, values)];

            // Deferred projection: evaluate against this invocation's outer row.
            var perCall = new SqlValue[expressions.Count];
            var runtime = new RuntimeContext(Resolve, batch);
            try
            {
                for (var i = 0; i < expressions.Count; i++)
                {
                    var raw = expressions[i].Run(runtime);
                    perCall[i] = raw.IsNull || raw.Type == schema[i] ? raw : raw.CoerceTo(schema[i]);
                }
            }
            catch (SimulatedSqlException projectionError)
            {
                projectionError.RaisedInRowProjection = true;
                throw;
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
            VolatileColumns = VolatileProjection.Of(expressions, fixesValues: false),
            IsBareConstantRow = isBareConstantRow,
            IsSingleConstantRow = isBareConstantRow,
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
            ColumnIsUntypedNull = UntypedNullsOf(expressions),
            ColumnReportsNumeric = ColumnReportsNumericOf(expressions, schema),
            ColumnAliasTypes = ColumnAliasTypesOf(expressions),
            ColumnMasks = columnMasks,
            // A FROM-less projection has no sources, so column nullability is
            // the per-expression rule alone (literals NOT NULL, other
            // expressions nullable) — matching real's result metadata
            // (`select 1` → Int, not IntN). The resolver is never consulted
            // (no column can appear without a source).
            ColumnNullability = ComputeColumnNullability(expressions, [], [], groupingSetsWritten: false, parseBatch, TypeResolver),
        };
    }

    /// <summary>
    /// A qualified star (<c>t.*</c>, <c>dbo.t.*</c>) as a whole projection
    /// element, the one place real accepts it: inside an expression, a
    /// function's argument list included, it is a syntax error near the
    /// <c>*</c>, and whatever follows the element is judged as what follows
    /// any element (probed 2026-09-26 against SQL Server 2025). Leaves the
    /// cursor past the <c>*</c>; null, with the cursor unmoved, for anything
    /// else.
    /// </summary>
    private static StarProjection? TryParseQualifiedStar(ParserContext context, QueryScope scope)
    {
        if (context.Token is not Name first)
            return null;
        var checkpoint = context.SaveCheckpoint();
        var name = new MultiPartName(first.Value);
        while (context.GetNextOptional() is Operator { Character: '.' })
        {
            switch (context.GetNextOptional())
            {
                case Name part when name.Count < 4:
                    name = name.WithAddedPart(part.Value);
                    continue;
                case Operator { Character: '*' }:
                    RejectStarInSchemaBoundBody(context, scope, qualified: true);
                    context.MoveNextOptional();
                    return new StarProjection(name.Leaf, name.ToString(), name);
            }

            break;
        }

        context.RestoreCheckpoint(checkpoint);
        return null;
    }

    /// <summary>
    /// Msg 1054 for a select-list star — bare or qualified, never
    /// <c>COUNT(*)</c>, <c>CHECKSUM(*)</c> or an <c>OUTPUT</c> clause's
    /// <c>inserted.*</c> — in a schema-bound view's or function's body, raised
    /// with the cursor on the <c>*</c>. Real refuses it as its parser reads it,
    /// so it outranks everything the body binds, and recovers past it the way
    /// it does past a syntax error (probed 2026-09-30 against SQL Server 2025).
    /// The state tells a statement's own query in a function's statement list
    /// (1 bare, 2 qualified) from a defining query or a nested one (6, 7).
    /// </summary>
    private static void RejectStarInSchemaBoundBody(ParserContext context, QueryScope scope, bool qualified)
    {
        if (context.SchemaBoundBody == SchemaBoundBody.None)
            return;
        var statementQuery = context.SchemaBoundBody == SchemaBoundBody.Statements
            && scope.Position is QueryPosition.Statement or QueryPosition.InsertSource or QueryPosition.ParenthesizedInsertSource;
        throw SimulatedSqlException.SyntaxNotAllowedInSchemaBoundObject("*", (statementQuery, qualified) switch
        {
            (true, false) => 1,
            (true, true) => 2,
            (false, false) => 6,
            (false, true) => 7,
        });
    }

    /// <summary>
    /// Names a select item that is a bare <c>$identity</c> or <c>$rowguid</c>
    /// after the column it reads, as real's result does (probed 2026-10-06
    /// against SQL Server 2025).
    /// </summary>
    private static void NameKeyPseudoColumns(List<Expression> expressions, List<FromSource> sources)
    {
        for (var i = 0; i < expressions.Count; i++)
        {
            if (expressions[i] is not Reference { ReferencedName: var name } reference || !HeapColumn.IsKeyPseudoName(name))
                continue;
            var (s, c) = FindSourceColumn([.. sources], name);
            if (s >= 0)
                expressions[i] = new NamedExpression(reference, sources[s].ColumnNames[c]);
        }
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
                    if (source.Qualifier is { } q && collation.Equals(q, star.Qualifier)
                        && (star.Prefix is not { Count: >= 2 } prefix || source.AnswersPrefix(prefix, prefix.Count)))
                    {
                        matched = source;
                        break;
                    }
                }
                // Real names the prefix as written (probed 2026-09-26 against
                // SQL Server 2025: `dbo.nosuch.*` is Msg 107 on 'dbo.nosuch').
                if (matched is null)
                    throw SimulatedSqlException.ColumnPrefixDoesNotMatch(star.WrittenQualifier!);
                AppendSourceColumns(expanded, matched);
            }

            expressions.RemoveAt(i);
            expressions.InsertRange(i, expanded);
        }

        static void AppendSourceColumns(List<Expression> destination, FromSource source)
        {
            // A sparse column set stands in for its sparse columns (probed
            // 2026-10-06 against SQL Server 2025).
            var hasColumnSet = Array.Exists(source.Columns, static column => column.IsColumnSet);
            for (var i = 0; i < source.ColumnNames.Length; i++)
            {
                // SELECT * excludes hidden columns (the period columns on a
                // system-versioned temporal table). Probe-confirmed against
                // SQL Server 2025: `select * from <temporal>` returns the
                // non-hidden columns; explicit references continue to bind.
                if (source.Columns[i].IsHidden || (hasColumnSet && source.Columns[i].IsSparse))
                    continue;
                var col = source.ColumnNames[i];
                Expression reference = source.Qualifier is { } q
                    ? new Reference(q, col)
                    : new Reference(col);
                destination.Add(ShadowedColumnDisplayName(col) is { } written ? new NamedExpression(reference, written) : reference);
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
    private static TemporalRowSource? ParseOptionalForSystemTime(ParserContext context, HeapTable? heapTable)
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
        // A source deferred while compiling (heapTable null) still parses the
        // clause: its grammar is checked then, the table when the statement runs.
        if (heapTable is not null && (heapTable.SystemVersioning is null || heapTable.PeriodColumns is null))
            throw SimulatedSqlException.ForSystemTimeRequiresVersionedTable(QualifiedNameFor(context, heapTable));

        var clause = ParseForSystemTimeArguments(context);
        return heapTable is null
            ? null
            : new TemporalRowSource(heapTable, heapTable.SystemVersioning!, heapTable.PeriodColumns!.Value, clause.Kind, clause.Lower, clause.Upper);
    }

    /// <summary>
    /// Peeks past the name at the cursor for <c>FOR SYSTEM_TIME</c> and parses
    /// the clause when it is there, leaving the cursor past it; otherwise
    /// leaves the cursor where it was and returns null.
    /// </summary>
    private static ForSystemTimeClause? ParseOptionalForSystemTimeClause(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var forToken = context.GetNextOptional();
        var systemTimeToken = forToken is ReservedKeyword { Keyword: Keyword.For } ? context.GetNextOptional() : null;
        if (systemTimeToken is UnquotedString { ContextualKeyword: ContextualKeyword.System_Time })
            return ParseForSystemTimeArguments(context);
        context.RestoreCheckpoint(checkpoint);
        return null;
    }

    /// <summary>
    /// Parses a <c>FOR SYSTEM_TIME</c> clause's form and bounds, the cursor on
    /// <c>SYSTEM_TIME</c> on entry and on the token past the clause on exit.
    /// </summary>
    private static ForSystemTimeClause ParseForSystemTimeArguments(ParserContext context)
    {
        context.MoveNextRequired();
        TemporalQueryKind kind;
        Expression? lower = null;
        Expression? upper = null;
        switch (context.Token)
        {
            // ALL: union of current + history rows, with only the
            // zero-duration filter every form applies.
            case ReservedKeyword { Keyword: Keyword.All }:
                context.MoveNextOptional();
                kind = TemporalQueryKind.All;
                break;
            // AS OF t: rows where start <= t < end.
            case ReservedKeyword { Keyword: Keyword.As }:
                context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Of })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                kind = TemporalQueryKind.AsOf;
                lower = ParseTemporalTimeArgument(context);
                break;
            // BETWEEN t1 AND t2: rows active at any point in [t1, t2].
            case ReservedKeyword { Keyword: Keyword.Between }:
                context.MoveNextRequired();
                lower = ParseTemporalTimeArgument(context);
                if (context.Token is not ReservedKeyword { Keyword: Keyword.And })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                kind = TemporalQueryKind.Between;
                upper = ParseTemporalTimeArgument(context);
                break;
            // FROM t1 TO t2: same, with the upper bound exclusive.
            case ReservedKeyword { Keyword: Keyword.From }:
                context.MoveNextRequired();
                lower = ParseTemporalTimeArgument(context);
                if (context.Token is not ReservedKeyword { Keyword: Keyword.To })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                kind = TemporalQueryKind.FromTo;
                upper = ParseTemporalTimeArgument(context);
                break;
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
                lower = ParseTemporalTimeArgument(context);
                if (context.Token is not Operator { Character: ',' })
                    throw TemporalSyntaxError(context);
                context.MoveNextRequired();
                upper = ParseTemporalTimeArgument(context);
                if (context.Token is not Operator { Character: ')' })
                    throw TemporalSyntaxError(context);
                context.MoveNextOptional();
                kind = TemporalQueryKind.ContainedIn;
                break;
            // Any other word is read as the form's name, so the token after it
            // is the one refused (probed 2026-10-06 against SQL Server 2025).
            case UnquotedString:
                var unknownForm = context.SaveCheckpoint();
                if (context.GetNextOptional() is null)
                    context.RestoreCheckpoint(unknownForm);
                throw TemporalSyntaxError(context);
            default:
                throw TemporalSyntaxError(context);
        }

        return new ForSystemTimeClause(kind, lower, upper);
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
        // A number is typed as the narrowest of tinyint / smallint / int it
        // fits, numeric past them or with a fraction, and clashes with the
        // period's datetime2 as the statement compiles (probed 2026-10-06
        // against SQL Server 2025).
        if (context.Token is Numeric clashing)
        {
            var literalType = clashing.Value.Type switch
            {
                DecimalSqlType => "numeric",
                _ when clashing.Value.IsNull => "int",
                BigIntSqlType => "numeric",
                _ => clashing.Value.AsInt32 switch
                {
                    <= byte.MaxValue => "tinyint",
                    <= short.MaxValue => "smallint",
                    _ => "int",
                },
            };
            throw SimulatedSqlException.OperandTypeClash("datetime2", literalType);
        }
        var argument = context.Token switch
        {
            Literal literal => new Value(literal.Value),
            AtPrefixedString atPrefixed => new VariableReference(atPrefixed, context),
            ReservedKeyword { Keyword: Keyword.Null } => new Value(),
            // An ODBC escape is a literal too (probed 2026-10-04 against SQL
            // Server 2025: {ts '…'}).
            Operator { Character: '{' } => Expression.ParseOdbcEscape(context),
            _ => throw TemporalSyntaxError(context),
        };
        // A variable's declared type is judged as the statement compiles, so a
        // module body refuses one at CREATE (probed 2026-10-04 against SQL
        // Server 2025: an int procedure parameter is Msg 206).
        if (argument is VariableReference { DeclaredType: { Category: SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate or SqlTypeCategory.UniqueIdentifier } declared })
        {
            throw SimulatedSqlException.OperandTypeClash(SqlType.GetDateTime2(7), declared);
        }
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
    /// <c>dbo</c> with the host database name <c>tempdb</c>. Real SQL Server
    /// likely writes a temp table's padded internal name here
    /// (<see cref="HeapTable.InternalName"/>), as it does in Msg 2628, but
    /// these two messages weren't probed for it, so the written name stays.
    /// </summary>
    private static string QualifiedNameFor(ParserContext context, HeapTable heapTable)
    {
        if (heapTable.Name.StartsWith('#'))
            return $"tempdb.dbo.{heapTable.Name}";
        var db = context.Batch.CurrentDatabase;
        var schemaName = db.Schemas.EnumerateValues().FirstOrDefault(s => s.SchemaId == heapTable.SchemaId)?.Name ?? Database.DefaultSchemaName;
        return $"{db.Name}.{schemaName}.{heapTable.Name}";
    }
}

/// <summary>A parsed <c>FOR SYSTEM_TIME</c> clause: its form and up to two bound expressions.</summary>
internal readonly struct ForSystemTimeClause(TemporalQueryKind kind, Expression? lower, Expression? upper)
{
    public readonly TemporalQueryKind Kind = kind;
    public readonly Expression? Lower = lower;
    public readonly Expression? Upper = upper;
}

/// <summary>
/// The <c>FOR SYSTEM_TIME</c> a view reference applies to its body, its bounds
/// evaluated where the reference is (null while the body only binds), and
/// whether any system-versioned table of the body took it — one that none
/// does is Msg 13544 naming the view.
/// </summary>
internal sealed class InheritedSystemTime(TemporalQueryKind kind, SqlValue? lower, SqlValue? upper)
{
    public readonly TemporalQueryKind Kind = kind;
    public readonly SqlValue? Lower = lower;
    public readonly SqlValue? Upper = upper;
    public bool Applied;
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
    Expression? upperBound) : PerExecutionRows
{
    public override IEnumerable<byte[]> For(BatchContext batch)
    {
        // Evaluate the bounds once at iteration start. A NULL bound makes
        // every comparison unknown, so the whole source is empty (real
        // returns no rows rather than raising).
        // AS OF's bound meets the period start first, in a <= comparison
        // (probed 2026-10-04 against SQL Server 2025).
        var lower = TemporalRowSource.EvaluateBound(lowerBound, batch, kind == TemporalQueryKind.AsOf ? "less than or equal to" : "greater than");
        var upper = TemporalRowSource.EvaluateBound(upperBound, batch);
        if ((lowerBound is not null && lower is null) || (upperBound is not null && upper is null))
            yield break;

        var startStored = parent.StorageOrdinals[period.StartOrdinal];
        var endStored = parent.StorageOrdinals[period.EndOrdinal];
        var lowerTime = lower ?? default;
        var upperTime = upper ?? default;

        // The current table's filter predicate hides its rows; the history
        // rows answer only to a predicate on the history table itself (probed
        // 2026-10-04 against SQL Server 2025).
        var currentFilter = RowSecurity.For(batch, parent)?.Filter is { } parentPredicate ? SecurityPredicateRunner.For(batch, parentPredicate) : null;
        var historyFilter = RowSecurity.For(batch, history)?.Filter is { } historyPredicate ? SecurityPredicateRunner.For(batch, historyPredicate) : null;

        // The current rows come in their clustered key's order, ahead of the
        // history's, as real's scan of the two reads them (probed 2026-10-04
        // against SQL Server 2025).
        foreach (var bytes in ClusteredScan.Rows(parent))
        {
            if (this.RowMatches(parent.StoredColumns, bytes, parent.Heap, startStored, endStored, lowerTime, upperTime, DateTime.MinValue)
                && currentFilter?.AdmitsStored(bytes) != false)
            {
                yield return bytes;
            }
        }
        // A finite HISTORY_RETENTION_PERIOD hides history rows whose validity
        // ended before the window opens. Real applies the same cutoff at query
        // time (its background cleanup task deletes them later), so an aged-out
        // version disappears from every FOR SYSTEM_TIME form the moment the
        // retention period is set.
        var cutoff = parent.HistoryRetentionCutoff(batch.CurrentStatement.UtcNow) ?? DateTime.MinValue;
        // The consumer decodes every row of this source against the parent's
        // heap, so a history row's off-row values — chains on the history
        // heap — are brought inline first; read through the parent, their
        // page indexes would name its unrelated chains.
        foreach (var bytes in ClusteredScan.Rows(history))
        {
            if (this.RowMatches(history.StoredColumns, bytes, history.Heap, startStored, endStored, lowerTime, upperTime, cutoff)
                && historyFilter?.AdmitsStored(bytes) != false)
            {
                yield return RowEncoder.EncodeRow(history.StoredColumns, RowDecoder.DecodeRow(history.StoredColumns, bytes, history.Heap));
            }
        }
    }

    /// <summary>
    /// Evaluates one bound to a <c>datetime2(7)</c> point, or null when the
    /// expression is absent or evaluates to NULL. The argument's type is
    /// gated the way real gates it — as a comparison against the period
    /// columns: strings and the date/time family (except <c>time</c>)
    /// convert, <c>time</c> and binary raise Msg 402, everything else
    /// (integer, decimal, money, float, bit, uniqueidentifier) raises
    /// Msg 206.
    /// </summary>
    internal static DateTime? EvaluateBound(Expression? expression, BatchContext batch, string operatorName = "greater than")
    {
        if (expression is null)
            return null;
        // The restricted argument grammar admits no column reference, so
        // the resolver is a guard rather than a reachable path.
        var raw = expression.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), batch));
        if (raw.IsNull)
            return null;
        // A sql_variant converts by the value it holds (probed 2026-10-04
        // against SQL Server 2025).
        if (raw.Type is SqlVariantSqlType)
        {
            raw = raw.AsVariantInner;
            if (raw.IsNull)
                return null;
        }
        var target = SqlType.GetDateTime2(7);
        if (raw.Type is TimeSqlType or BinarySqlType or VarbinarySqlType)
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(target, raw.Type, operatorName);
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
        // period predicate only sees rows that were current for a while; a row
        // ending before it starts, which only an adopted history holds, stays
        // visible to ALL (probed 2026-10-04 against SQL Server 2025).
        return rowStart != rowEnd && rowEnd >= retentionCutoff && kind switch
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

    /// <summary>
    /// Whether the term is a bare column name (parentheses aside), the only
    /// shape that may name a select-list alias: real binds a name inside any
    /// larger expression — <c>x + 1</c>, <c>-x</c>, <c>x COLLATE …</c>, a
    /// CASE — to the FROM sources alone, so it is Msg 207 when only an alias
    /// carries it and reads the source column when both do. Probed against
    /// SQL Server 2025 (2026-09-24).
    /// </summary>
    public readonly bool MayNameAlias;

    public bool IsOrdinal => this.Expr is null;

    private OrderBySpec(Expression? expr, int ordinal, bool descending)
    {
        this.Expr = expr;
        this.Ordinal = ordinal;
        this.Descending = descending;
        this.MayNameAlias = IsBareReference(expr);
    }

    private static bool IsBareReference(Expression? expr) => expr switch
    {
        Expressions.Parenthesized p => IsBareReference(p.Wrapped),
        Expressions.Reference => true,
        _ => false,
    };

    public static OrderBySpec FromExpression(Expression expr, bool descending) => new(expr, 0, descending);
    public static OrderBySpec FromOrdinal(int ordinal, bool descending) => new(null, ordinal, descending);
}
