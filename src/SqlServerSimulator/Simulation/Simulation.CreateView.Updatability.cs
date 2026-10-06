using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Computes the updatability metadata for a freshly-parsed view body —
    /// the eventual base <see cref="HeapTable"/>, per-output-column base-
    /// ordinal map, and pre-bound visibility / CHECK OPTION closures. Walks
    /// view-on-view chains by composing through each intermediate view's
    /// own pre-computed metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A view is updatable iff every level in its chain satisfies:
    /// </para>
    /// <list type="bullet">
    /// <item>No DISTINCT / aggregates / GROUP BY / HAVING / set ops /
    /// window functions (<see cref="Selection.UpdatabilityProfile"/> is
    /// non-null), and exactly one FROM source.</item>
    /// <item>The single source is either a heap table or another updatable
    /// view (<see cref="View.BaseTable"/> non-null).</item>
    /// <item>Every column referenced inside any WHERE clause up the chain
    /// maps to a real base-table column (no WHERE that references a
    /// derived projection above it).</item>
    /// </list>
    /// <para>
    /// A body reading several sources collapses to none of the four — its
    /// WHERE and join predicates read columns of more than one table, so
    /// there is no single base row to evaluate them against. Such a view
    /// records <see cref="ViewUpdatabilityRejection.MultipleSources"/>
    /// (Msg 4405, what DELETE raises) plus <c>IsJoinUpdatable</c>, which
    /// routes UPDATE and INSERT to the chain-walking paths in
    /// <c>Simulation.JoinViewDml.cs</c> — those re-parse each level's body
    /// and work off the live profiles. A single-source view reading such a
    /// view carries the same pair, so the chain can be any depth.
    /// </para>
    /// </remarks>
    private static (HeapTable? BaseTable, int[] BaseColumnOrdinals, ViewUpdatabilityRejection Rejection, Func<SqlValue[], BatchContext, bool>? VisibilityCheck, Func<SqlValue[], BatchContext, bool>? CheckOptionCheck, bool IsJoinUpdatable, View? PartitionedBase)
        AnalyzeViewUpdatability(Collation collation, Selection bodySelection, bool withCheckOption, bool correlated = false)
    {
        if (bodySelection.UpdatabilityProfile is not { } profile)
            return (null, [], bodySelection.UpdatabilityRejection, null, null, false, null);

        if (profile.Sources.Length > 1)
            return (null, [], ViewUpdatabilityRejection.MultipleSources, null, null, true, null);

        var source = profile.Sources[0];
        HeapTable? baseTable = null;
        View? partitionedBase = null;
        View? upstreamLevel = null;
        int[] sourceColumnToBaseOrdinal;
        Func<SqlValue[], BatchContext, bool>? upstreamVisibility = null;
        Func<SqlValue[], BatchContext, bool>? upstreamCheckOption = null;

        if (source.BackingTable is { } table)
        {
            baseTable = table;
            sourceColumnToBaseOrdinal = new int[source.Columns.Length];
            for (var i = 0; i < source.Columns.Length; i++)
                sourceColumnToBaseOrdinal[i] = i;
        }
        else if (source.UpdatableView() is { BaseTable: { } upstreamBaseTable } upstreamView)
        {
            baseTable = upstreamBaseTable;
            upstreamLevel = upstreamView;
            sourceColumnToBaseOrdinal = upstreamView.BaseColumnOrdinals;
            upstreamVisibility = upstreamView.VisibilityCheck;
            upstreamCheckOption = upstreamView.CheckOptionCheck;
        }
        else if (source.UpdatableView() is { PartitionedBase: { } partitioned } partitionedLevel)
        {
            // A level over a partitioned view composes through that view's
            // columns as it would through a base table's: real routes a write
            // through it to the members (probed 2026-10-01 against SQL Server
            // 2025, through a view, a CTE and a derived table).
            partitionedBase = partitioned;
            upstreamLevel = partitionedLevel;
            sourceColumnToBaseOrdinal = partitionedLevel.BaseColumnOrdinals;
            upstreamVisibility = partitionedLevel.VisibilityCheck;
            upstreamCheckOption = partitionedLevel.CheckOptionCheck;
        }
        else if (source.UpdatableView() is { IsJoinUpdatable: true })
        {
            // The chain bottoms out in a multi-source view, which has no
            // single base table for this level to compose through. The write
            // walks the level stack at the statement instead, so this level
            // carries the same pair the join view itself does.
            return (null, [], ViewUpdatabilityRejection.MultipleSources, null, null, true, null);
        }
        else
        {
            // A view or CTE over a set operation refuses as that body does
            // (probed 2026-10-01 against SQL Server 2025: a CTE over a UNION
            // CTE is Msg 4426 naming the outer one), one over VALUES or a
            // rowset function takes every column as derived; an APPLY's
            // correlated body, TVF, catalog view or other non-updatable view
            // supports no DML pass-through.
            return (null, [], UnionRejectionOf(source)
                ?? (source.ConstructsRows || source.UpdatableView() is { RejectionReason: ViewUpdatabilityRejection.ConstructedRows }
                    ? ViewUpdatabilityRejection.ConstructedRows
                    : ViewUpdatabilityRejection.UnsupportedShape), null, null, false, null);
        }

        var baseColumnOrdinals = new int[profile.Projections.Length];
        for (var i = 0; i < profile.Projections.Length; i++)
        {
            // A projection is "direct" if its outer shape unwraps to a bare
            // Reference (the wrapper may be NamedExpression from `AS alias`).
            // Anything else — arithmetic, function call, CAST, literal —
            // is a derived field; touching it at INSERT/UPDATE triggers
            // Msg 4406 at the DML site (gated per-column there, since DELETE
            // through a view with derived columns still works).
            if (UnwrapDirectRef(profile.Projections[i]) is { ReferencedName: { } refName })
            {
                var sourceOrd = -1;
                for (var j = 0; j < source.ColumnNames.Length; j++)
                {
                    if (collation.Equals(source.ColumnNames[j], refName.Leaf))
                    {
                        sourceOrd = j;
                        break;
                    }
                }
                baseColumnOrdinals[i] = sourceOrd >= 0 ? sourceColumnToBaseOrdinal[sourceOrd] : -1;
            }
            else
            {
                baseColumnOrdinals[i] = -1;
            }
        }

        // Build the column-name → base-ordinal dict for this level's WHERE
        // resolution. Each WHERE excluder references columns by the upstream
        // view's OutputColumn names (or the base table's column names when
        // the source is a heap). Translation uses sourceColumnToBaseOrdinal.
        var nameToBaseOrdinal = new Dictionary<string, int>(collation);
        for (var j = 0; j < source.ColumnNames.Length; j++)
            nameToBaseOrdinal[source.ColumnNames[j]] = sourceColumnToBaseOrdinal[j];

        // An APPLY's correlated body filters its rows against the left side's
        // current row as the join runs them, so its WHERE is no check a base
        // row can be put to on its own.
        var excluders = correlated ? [] : profile.Excluders;
        // A WHERE reading a column the view below derives judges each base
        // row by that view's projection — or, below a windowed or row-limited
        // view, is left to the body's own run, which picks the rows the write
        // reaches (probed 2026-10-06 against SQL Server 2025).
        var derivedReader = upstreamLevel is { IsWindowed: false, IsRowLimited: false, BaseTable: { } readTable }
            ? DerivedColumnReader(upstreamLevel, readTable, source.ColumnNames, collation)
            : null;
        List<BooleanExpression>? checkedExcluders = null;
        foreach (var excluder in excluders)
        {
            var unmappable = false;
            excluder.VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
            {
                if (!nameToBaseOrdinal.TryGetValue(name.Leaf, out var ord) || ord < 0)
                    unmappable = true;
            }));
            if (!unmappable || derivedReader is not null)
            {
                (checkedExcluders ??= []).Add(excluder);
                continue;
            }
            if (upstreamLevel is null || withCheckOption || upstreamLevel is { BaseTable: null, IsWindowed: false, IsRowLimited: false })
                return (null, [], ViewUpdatabilityRejection.UnsupportedShape, null, null, false, null);
        }

        var thisLevelCheck = MakeWhereCheck(checkedExcluders is null ? [] : [.. checkedExcluders], nameToBaseOrdinal, derivedReader);

        var combinedVisibility = ComposeAnd(thisLevelCheck, upstreamVisibility);

        // CHECK OPTION enforcement: each level with WITH CHECK OPTION
        // contributes its own visibility (= its WHERE composed with its
        // upstream visibility). Upstream's CHECK OPTION composes in
        // unchanged so deeper-chain check options still fire.
        var thisLevelCheckOption = withCheckOption ? combinedVisibility : null;
        var combinedCheckOption = ComposeAnd(thisLevelCheckOption, upstreamCheckOption);

        // A level over a partitioned view keeps the UNION ALL refusal for every
        // path that doesn't route to the members.
        var rejection = partitionedBase is null ? ViewUpdatabilityRejection.None : ViewUpdatabilityRejection.UnionAll;
        return (baseTable, baseColumnOrdinals, rejection, combinedVisibility, combinedCheckOption, false, partitionedBase);
    }

    /// <summary>
    /// The view a single-source updatable view reads, and each of its output
    /// columns' ordinal in that view's own output (<c>-1</c> for a derived
    /// column) — the per-level link the ownership chain walks, since
    /// <see cref="View.BaseColumnOrdinals"/> composes the whole chain down to
    /// the base table. Null upstream when the body reads a table directly.
    /// </summary>
    private static (View? Upstream, int[] Ordinals) UpstreamLinkOf(Collation collation, Selection bodySelection)
    {
        if (bodySelection.UpdatabilityProfile is not { Sources: [{ BackingView: { } upstream } source] } profile)
            return (null, []);
        var ordinals = new int[profile.Projections.Length];
        for (var i = 0; i < ordinals.Length; i++)
        {
            ordinals[i] = -1;
            if (UnwrapDirectRef(profile.Projections[i]) is not { ReferencedName: { } refName })
                continue;
            for (var j = 0; j < source.ColumnNames.Length; j++)
            {
                if (collation.Equals(source.ColumnNames[j], refName.Leaf))
                {
                    ordinals[i] = j;
                    break;
                }
            }
        }
        return (upstream, ordinals);
    }

    /// <summary>
    /// Returns the underlying <see cref="Reference"/> when <paramref name="expr"/>
    /// is a direct column reference (possibly wrapped in one or more
    /// <see cref="NamedExpression"/> layers from <c>AS alias</c>). Null
    /// otherwise — any other wrapper (arithmetic, CAST, function call,
    /// CASE, etc.) means the projection is derived. Same shape as the
    /// equivalent helper in <c>Selection.SelectInto.cs</c>; kept separate
    /// so the SELECT INTO logic isn't entangled with view DML.
    /// </summary>
    private static Reference? UnwrapDirectRef(Expression expr) => expr switch
    {
        Reference r => r,
        NamedExpression named => UnwrapDirectRef(named.Inner),
        _ => null,
    };

    /// <summary>
    /// The set-operation refusal a source's body carries — its own when it is
    /// a set operation read as a derived table, or its view's or CTE's — or
    /// null when it carries none.
    /// </summary>
    private static ViewUpdatabilityRejection? UnionRejectionOf(FromSource source)
    {
        var rejection = source.UpdatableView()?.RejectionReason
            ?? (source is { LateralIsQueryBody: true, LateralPlan: { } plan } ? plan.UpdatabilityRejection : ViewUpdatabilityRejection.None);
        return IsUnionRejection(rejection) ? rejection : null;
    }

    /// <summary>
    /// Whether a body's refusal comes of a <c>UNION</c> in it, which makes every
    /// column derived: an <c>UPDATE</c> or <c>INSERT</c> through it is Msg 4406
    /// whichever columns it names (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    private static bool IsUnionRejection(ViewUpdatabilityRejection rejection) =>
        rejection is ViewUpdatabilityRejection.Union or ViewUpdatabilityRejection.UnionAll or ViewUpdatabilityRejection.SetOperationOverUnion;

    /// <summary>
    /// <see cref="View.UnionLeadsWithJoin"/> for a view over <paramref name="body"/>.
    /// </summary>
    private static bool UnionLeadsWithJoinOf(Selection body, ViewUpdatabilityRejection rejection) =>
        rejection is ViewUpdatabilityRejection.Union or ViewUpdatabilityRejection.UnionAll
        && (body.UpdatabilityProfile is { Sources: [var source] }
            ? source.UpdatableView() is { UnionLeadsWithJoin: true }
            : body.BranchFromSources is { Length: > 1 });

    /// <summary>
    /// <see cref="View.UnionOwnerName"/> for a view over <paramref name="body"/>:
    /// when its single source is a view, the stored view that source's
    /// <c>UNION</c> belongs to — that view itself, or the one it reads.
    /// </summary>
    private static string? UnionOwnerNameOf(Selection body, ViewUpdatabilityRejection rejection) =>
        IsUnionRejection(rejection) && body.UpdatabilityProfile is { Sources: [var source] } && source.UpdatableView() is { } inner
            ? inner.UnionOwnerName ?? (inner.UnstoredBody is null ? inner.Name : null)
            : null;

    /// <summary>
    /// <see cref="View.DerivedOutputColumns"/> for a body that names no single
    /// base table: every column for a body over a <c>UNION</c>, else
    /// <see cref="DerivedOutputColumnsOf"/>, and null for a body reaching one
    /// base table or reading several.
    /// </summary>
    private static bool[]? DerivedOutputColumnsFor(Selection body, HeapTable? baseTable, ViewUpdatabilityRejection rejection, int width)
    {
        if (baseTable is not null || rejection == ViewUpdatabilityRejection.MultipleSources)
            return null;
        if (!IsUnionRejection(rejection) && rejection != ViewUpdatabilityRejection.ConstructedRows)
            return DerivedOutputColumnsOf(body);
        var derived = new bool[width];
        Array.Fill(derived, true);
        return derived;
    }

    /// <summary>
    /// Builds a per-level WHERE evaluator: given a base-table row's
    /// <see cref="SqlValue"/> array (indexed by base-table column ordinal)
    /// and a <see cref="BatchContext"/>, returns true iff every excluder
    /// evaluates to <c>true</c> (the WHERE-style three-valued rule, where
    /// UNKNOWN excludes). Returns null when <paramref name="excluders"/>
    /// is empty so callers can avoid wrapping a no-op closure.
    /// </summary>
    private static Func<SqlValue[], BatchContext, bool>? MakeWhereCheck(
        BooleanExpression[] excluders,
        Dictionary<string, int> nameToBaseOrdinal,
        Func<SqlValue[], string, BatchContext, SqlValue?>? derivedReader = null) => excluders.Length == 0
            ? null
            : (row, batch) =>
            {
                SqlValue Resolve(MultiPartName name) => nameToBaseOrdinal.TryGetValue(name.Leaf, out var ord) && ord >= 0
                    ? row[ord]
                    : derivedReader?.Invoke(row, name.Leaf, batch) ?? throw SimulatedSqlException.InvalidColumnName(name);
                var runtime = new RuntimeContext(Resolve, batch);
                foreach (var excluder in excluders)
                {
                    if (excluder.Run(runtime) != true)
                        return false;
                }
                return true;
            };

    /// <summary>
    /// Reads a column <paramref name="upstream"/> derives off a row of its
    /// base table, by name among <paramref name="sourceColumnNames"/>, through
    /// the view's projections (<see cref="SingleBaseViewReader"/>); null for a
    /// name it doesn't carry.
    /// </summary>
    private static Func<SqlValue[], string, BatchContext, SqlValue?> DerivedColumnReader(View upstream, HeapTable table, string[] sourceColumnNames, Collation collation) =>
        (row, name, batch) =>
        {
            for (var j = 0; j < sourceColumnNames.Length; j++)
            {
                if (collation.Equals(sourceColumnNames[j], name))
                    return SingleBaseViewReader(batch, upstream, table)(row, j);
            }
            return null;
        };

    /// <summary>
    /// Composes two boolean closures into an AND. Either argument may be
    /// null (treated as <c>true</c>). When both are null, returns null —
    /// the caller treats that as "no predicate to evaluate, row is
    /// always visible". Avoids capturing always-true predicates in the
    /// chain so the common no-WHERE view runs without per-row closure
    /// invocation.
    /// </summary>
    private static Func<SqlValue[], BatchContext, bool>? ComposeAnd(
        Func<SqlValue[], BatchContext, bool>? a,
        Func<SqlValue[], BatchContext, bool>? b) => (a, b) switch
        {
            (null, _) => b,
            (_, null) => a,
            _ => (row, batch) => a(row, batch) && b(row, batch),
        };

    /// <summary>
    /// <see cref="View.DerivedOutputColumns"/> for a view body: a column is
    /// derived unless it is a bare reference to a source column that isn't
    /// itself an underlying view's derived column. Null when the body kept no
    /// projection to read (a set operation).
    /// </summary>
    private static bool[]? DerivedOutputColumnsOf(Selection bodySelection)
    {
        if (bodySelection.ProjectionExpressions is not { } expressions || bodySelection.BranchFromSources is not { } sources)
            return null;
        var derived = new bool[expressions.Length];
        for (var i = 0; i < expressions.Length; i++)
        {
            var expression = expressions[i] is NamedExpression named ? named.Inner : expressions[i];
            if (expression is not Reference reference)
            {
                derived[i] = true;
                continue;
            }
            var (s, c) = Selection.FindSourceColumn(sources, reference.ReferencedName);
            derived[i] = s < 0 || (sources[s].UpdatableView()?.DerivedOutputColumns is { } underlying && underlying[c]);
        }
        return derived;
    }

    /// <summary>
    /// Refuses an INSERT or UPDATE through a view with no base table to write,
    /// once its written columns are known, as real does: an unknown name is
    /// Msg 207, a derived column anywhere in the list Msg 4406, and otherwise
    /// the view's own refusal (Msg 4405 for several sources, Msg 4403 else).
    /// The written columns are read ahead without consuming them — the
    /// <c>SET</c> targets, or an INSERT's column list, whose absence names
    /// every column (probed 2026-09-25 against SQL Server 2025). Entered with
    /// the cursor on the token after the view's name; the messages name the
    /// view as the statement wrote it, as real's do.
    /// </summary>
    private static SimulatedSqlException RefuseNonUpdatableViewWrite(ParserContext context, View view, MultiPartName writtenName, bool isUpdate)
    {
        var viewLabel = writtenName.ToString();
        if (view.DerivedOutputColumns is { } derivedColumns)
        {
            var checkpoint = context.SaveCheckpoint();
            var written = isUpdate ? PeekSetTargets(context) : PeekInsertColumnList(context);
            context.RestoreCheckpoint(checkpoint);
            var anyDerived = false;
            if (written is null)
            {
                anyDerived = Array.IndexOf(derivedColumns, true) >= 0;
            }
            else
            {
                foreach (var column in written)
                {
                    var ordinal = Array.FindIndex(view.OutputColumns, c => context.CurrentDatabase.Collation.Equals(c.Name, column));
                    if (ordinal < 0)
                        return SimulatedSqlException.InvalidColumnName(column);
                    anyDerived |= derivedColumns[ordinal];
                }
            }
            if (anyDerived)
                return SimulatedSqlException.ViewDmlTouchesDerivedField(view.UnionOwnerName ?? viewLabel);
        }
        return NonUpdatableViewError(view, viewLabel);
    }

    /// <summary>
    /// Whether a view body limits its rows, directly or through the single
    /// view it reads (<see cref="View.IsRowLimited"/>).
    /// </summary>
    private static bool IsRowLimitedBody(Selection body) =>
        body.HasTopOrOffsetOrFetch
        || (body.UpdatabilityProfile is { Sources: [var source] } && source.UpdatableView() is { IsRowLimited: true });

    /// <summary>
    /// Whether a view body projects a window function, directly or through
    /// the single view it reads (<see cref="View.IsWindowed"/>).
    /// </summary>
    private static bool IsWindowedBody(Selection body) =>
        body.HasWindows
        || (body.UpdatabilityProfile is { Sources: [var source] } && source.UpdatableView() is { IsWindowed: true });

    /// <summary>
    /// The rows a windowed or row-limited view or CTE yields, keyed by the base
    /// row each came from, for an UPDATE / DELETE / MERGE through it: real
    /// writes to exactly those rows and reads the derived columns (a
    /// <c>ROW_NUMBER()</c>'s <c>rn</c>) off them (probed 2026-09-25 and
    /// 2026-09-30 against SQL Server 2025). Null when the target is neither, or
    /// the write is positioned. A row's trailing element, past the body's own
    /// columns, is the address it came from.
    /// </summary>
    /// <remarks>
    /// The body runs once, carrying each row's base address through its row
    /// limit or window stage as a projected value
    /// (<see cref="Selection.ExecuteWithRowAddresses"/>), so a limit choosing
    /// between rows its projection can't tell apart still names the row it
    /// chose — through a view, CTE or derived table the body reads too, whose
    /// rows carry the address the same way (probed 2026-10-01 against SQL
    /// Server 2025: <c>DELETE</c> through <c>SELECT TOP 1 id FROM v1 ORDER BY
    /// v</c> removes the one row the limit chose between two <c>id = 1</c>
    /// rows).
    /// </remarks>
    private static Dictionary<(int Page, int Slot), SqlValue[]>? MaterializeRowSelectiveViewRows(ParserContext context, View? view, bool positioned)
    {
        if (view is not ({ IsWindowed: true } or { IsRowLimited: true }) || positioned || context.Batch.IsSkipping)
            return null;
        var rows = new Dictionary<(int Page, int Slot), SqlValue[]>();
        foreach (var (row, address) in ViewRowsWithAddresses(context.Batch, view))
            rows[address ?? throw RowWithoutAddressNotModeled(view.Name)] = row;
        return rows;
    }

    /// <summary>
    /// The rows a single-base view or CTE yields, each with the address of the
    /// base row it shows: the body runs once, carrying the address through
    /// every stage (<see cref="Selection.ExecuteWithRowAddresses"/>), so a row
    /// limit or window applies exactly as a read of the view applies it. An
    /// address is null for a row that came from no heap row.
    /// </summary>
    private static List<(SqlValue[] Row, (int Page, int Slot)? Address)> ViewRowsWithAddresses(BatchContext batch, View view)
    {
        var body = batch.Connection.Simulation.ParseViewBodyPlan(batch, view);
        var width = body.ColumnNames.Length;
        var rows = new List<(SqlValue[] Row, (int Page, int Slot)? Address)>();
        foreach (var row in body.ExecuteWithRowAddresses(batch))
        {
            rows.Add(row.Length != width + 1 || row[width].IsNull
                ? (row, null)
                : (row, RowLocator.Unpack(row[width].AsInt64)));
        }
        return rows;
    }

    /// <summary>
    /// A write through a view whose body yielded a row carrying no base
    /// address, which only a body reaching no single base table could.
    /// </summary>
    private static NotSupportedException RowWithoutAddressNotModeled(string viewLabel) =>
        new($"DML through '{viewLabel}' isn't modeled for this shape: a row its body yields carries no base row to write.");

    /// <summary>The leaf names an UPDATE's SET list assigns, read from the cursor on; null when no SET follows.</summary>
    private static List<string>? PeekSetTargets(ParserContext context)
    {
        // Skip a table-hint group between the name and SET.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With } && context.GetNextOptional() is Operator { Character: '(' })
            SkipParenthesized(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Set })
            return null;
        var targets = new List<string>();
        do
        {
            if (context.GetNextOptional() is not Name target)
                return targets;
            var leaf = target.Value;
            while (context.GetNextOptional() is Operator { Character: '.' })
            {
                if (context.GetNextOptional() is Name part)
                    leaf = part.Value;
            }
            targets.Add(leaf);
            // The assigned expression runs to the next comma outside any
            // parentheses, or to the clause that ends the SET list.
            var depth = 0;
            var listContinues = false;
            while (!listContinues && context.GetNextOptional() is { } token)
            {
                switch (token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        break;
                    case Operator { Character: ')' }:
                        depth--;
                        break;
                    case Operator { Character: ',' } when depth == 0:
                        listContinues = true;
                        break;
                    case Operator { Character: ';' } when depth == 0:
                    case ReservedKeyword { Keyword: Keyword.From or Keyword.Where or Keyword.Option } when depth == 0:
                    case UnquotedString { ContextualKeyword: ContextualKeyword.Output } when depth == 0:
                        return targets;
                }
            }
        } while (context.Token is Operator { Character: ',' });
        return targets;
    }

    /// <summary>An INSERT's column list, read from the cursor on; null when none is written.</summary>
    private static List<string>? PeekInsertColumnList(ParserContext context)
    {
        if (context.Token is not Operator { Character: '(' })
            return null;
        var columns = new List<string>();
        while (context.GetNextOptional() is Name column)
        {
            columns.Add(column.Value);
            if (context.GetNextOptional() is not Operator { Character: ',' })
                break;
        }
        return columns;
    }

    /// <summary>Advances past the parenthesized group whose opening parenthesis is under the cursor.</summary>
    private static void SkipParenthesized(ParserContext context)
    {
        var depth = 1;
        while (depth > 0 && context.GetNextOptional() is { } token)
        {
            depth += token switch
            {
                Operator { Character: '(' } => 1,
                Operator { Character: ')' } => -1,
                _ => 0,
            };
        }
        _ = context.GetNextOptional();
    }

    /// <summary>
    /// Resolves a DML target named by one of the statement's own CTEs as the
    /// unstored view over the CTE's body (<see cref="CteDmlView"/>): real writes
    /// through a CTE exactly as through a view with that body — a plain
    /// single-table projection passes through to the table, one reading several
    /// sources writes the one base table its columns land in, a derived column
    /// is Msg 4406 and an aggregate body Msg 4403 (probed 2026-09-25 and
    /// 2026-10-01 against SQL Server 2025).
    /// </summary>
    private static bool TryResolveCteTarget(ParserContext context, MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out View? view)
    {
        view = null;
        if (name.Count != 1 || context.CteBindings is not { } bindings || !bindings.TryGetValue(name.Leaf, out var binding) || binding.Plan is null)
            return false;
        view = CteDmlView(binding);
        return true;
    }

    /// <summary>
    /// A CTE analyzed as the unstored view a write through it — or through a
    /// body reading it — passes down, with the same analysis <c>CREATE
    /// VIEW</c> runs; built once per binding, after the CTEs its body reads.
    /// </summary>
    internal static View CteDmlView(CteBinding binding) =>
        binding.DmlTarget ??= UnstoredDmlView(binding.Plan!, binding.Database, binding.Name, binding.ColumnNames, isDerivedTable: false);

    /// <summary>
    /// A derived table analyzed as the unstored view a write through it passes
    /// down, as <see cref="CteDmlView"/> analyzes a CTE; built once per
    /// binding.
    /// </summary>
    internal static View DerivedTableDmlView(DerivedTableBinding binding) =>
        binding.DmlTarget ??= UnstoredDmlView(binding.Body, binding.Database, binding.Alias, binding.ColumnNames, isDerivedTable: true, binding.Correlated);

    /// <summary>The unstored view over <paramref name="body"/> that a CTE or derived table named <paramref name="name"/> is to a write.</summary>
    private static View UnstoredDmlView(Selection body, Database database, string name, string[] columnNames, bool isDerivedTable, bool correlated = false)
    {
        var collation = database.Collation;
        var (baseTable, baseColumnOrdinals, rejection, visibilityCheck, checkOptionCheck, isJoinUpdatable, partitionedBase) = AnalyzeViewUpdatability(collation, body, withCheckOption: false, correlated);
        var outputColumns = ComputeViewOutputColumns(collation, body, [.. columnNames], name);
        return new View(
            database.Schemas[Database.DefaultSchemaName],
            name,
            objectId: 0,
            outputColumns,
            bodyText: string.Empty,
            withCheckOption: false,
            isSchemaBound: false,
            createDate: default,
            baseTable,
            baseColumnOrdinals,
            rejection,
            visibilityCheck,
            checkOptionCheck,
            isJoinUpdatable)
        {
            DerivedOutputColumns = DerivedOutputColumnsFor(body, baseTable, rejection, outputColumns.Length),
            UnionOwnerName = UnionOwnerNameOf(body, rejection),
            UnionLeadsWithJoin = UnionLeadsWithJoinOf(body, rejection),
            IsRowLimited = IsRowLimitedBody(body),
            IsWindowed = IsWindowedBody(body),
            VolatileColumns = body.VolatileColumns,
            UnstoredBody = body,
            IsDerivedTable = isDerivedTable,
            IsCorrelated = correlated,
            PartitionedBase = partitionedBase,
        };
    }
}
