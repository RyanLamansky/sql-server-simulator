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
    private static (HeapTable? BaseTable, int[] BaseColumnOrdinals, ViewUpdatabilityRejection Rejection, Func<SqlValue[], BatchContext, bool>? VisibilityCheck, Func<SqlValue[], BatchContext, bool>? CheckOptionCheck, bool IsJoinUpdatable)
        AnalyzeViewUpdatability(Collation collation, Selection bodySelection, bool withCheckOption)
    {
        if (bodySelection.UpdatabilityProfile is not { } profile)
            return (null, [], bodySelection.UpdatabilityRejection, null, null, false);

        if (profile.Sources.Length > 1)
            return (null, [], ViewUpdatabilityRejection.MultipleSources, null, null, true);

        var source = profile.Sources[0];
        HeapTable baseTable;
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
        else if (source.BackingView is { BaseTable: { } upstreamBaseTable } upstreamView)
        {
            baseTable = upstreamBaseTable;
            sourceColumnToBaseOrdinal = upstreamView.BaseColumnOrdinals;
            upstreamVisibility = upstreamView.VisibilityCheck;
            upstreamCheckOption = upstreamView.CheckOptionCheck;
        }
        else if (source.BackingView is { IsJoinUpdatable: true })
        {
            // The chain bottoms out in a multi-source view, which has no
            // single base table for this level to compose through. The write
            // walks the level stack at the statement instead, so this level
            // carries the same pair the join view itself does.
            return (null, [], ViewUpdatabilityRejection.MultipleSources, null, null, true);
        }
        else
        {
            // Source is a derived table, CTE, OPENJSON, TVF, catalog view,
            // or a non-updatable view — none of which support DML
            // pass-through.
            return (null, [], ViewUpdatabilityRejection.UnsupportedShape, null, null, false);
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

        foreach (var excluder in profile.Excluders)
        {
            var unmappable = false;
            excluder.VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
            {
                if (!nameToBaseOrdinal.TryGetValue(name.Leaf, out var ord) || ord < 0)
                    unmappable = true;
            }));
            if (unmappable)
                return (null, [], ViewUpdatabilityRejection.UnsupportedShape, null, null, false);
        }

        var thisLevelCheck = MakeWhereCheck(profile.Excluders, nameToBaseOrdinal);

        var combinedVisibility = ComposeAnd(thisLevelCheck, upstreamVisibility);

        // CHECK OPTION enforcement: each level with WITH CHECK OPTION
        // contributes its own visibility (= its WHERE composed with its
        // upstream visibility). Upstream's CHECK OPTION composes in
        // unchanged so deeper-chain check options still fire.
        var thisLevelCheckOption = withCheckOption ? combinedVisibility : null;
        var combinedCheckOption = ComposeAnd(thisLevelCheckOption, upstreamCheckOption);

        return (baseTable, baseColumnOrdinals, ViewUpdatabilityRejection.None, combinedVisibility, combinedCheckOption, false);
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
    /// Builds a per-level WHERE evaluator: given a base-table row's
    /// <see cref="SqlValue"/> array (indexed by base-table column ordinal)
    /// and a <see cref="BatchContext"/>, returns true iff every excluder
    /// evaluates to <c>true</c> (the WHERE-style three-valued rule, where
    /// UNKNOWN excludes). Returns null when <paramref name="excluders"/>
    /// is empty so callers can avoid wrapping a no-op closure.
    /// </summary>
    private static Func<SqlValue[], BatchContext, bool>? MakeWhereCheck(
        BooleanExpression[] excluders,
        Dictionary<string, int> nameToBaseOrdinal) => excluders.Length == 0
            ? null
            : (row, batch) =>
            {
                SqlValue Resolve(MultiPartName name) => nameToBaseOrdinal.TryGetValue(name.Leaf, out var ord) && ord >= 0
                    ? row[ord]
                    : throw SimulatedSqlException.InvalidColumnName(name);
                var runtime = new RuntimeContext(Resolve, batch);
                foreach (var excluder in excluders)
                {
                    if (excluder.Run(runtime) != true)
                        return false;
                }
                return true;
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
            derived[i] = s < 0 || (sources[s].BackingView?.DerivedOutputColumns is { } underlying && underlying[c]);
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
                return SimulatedSqlException.ViewDmlTouchesDerivedField(viewLabel);
        }
        return view.RejectionReason == ViewUpdatabilityRejection.MultipleSources
            ? SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewLabel)
            : SimulatedSqlException.CannotUpdateNonUpdatableView(viewLabel);
    }

    /// <summary>
    /// The name a Msg 4406 over an updatable view's derived column carries: a
    /// CTE target bare, as its statement wrote it, and a stored view
    /// schema-qualified — where real names the stored view as written too.
    /// </summary>
    private static string DerivedFieldViewLabel(View view) =>
        view.ObjectId == 0 ? view.Name : $"{view.Schema.Name}.{view.Name}";

    /// <summary>
    /// Whether a view body limits its rows, directly or through the single
    /// view it reads (<see cref="View.IsRowLimited"/>).
    /// </summary>
    private static bool IsRowLimitedBody(Selection body) =>
        body.HasTopOrOffsetOrFetch
        || (body.UpdatabilityProfile is { Sources: [{ BackingView.IsRowLimited: true }] });

    /// <summary>
    /// Whether a view body projects a window function, directly or through
    /// the single view it reads (<see cref="View.IsWindowed"/>).
    /// </summary>
    private static bool IsWindowedBody(Selection body) =>
        body.HasWindows
        || (body.UpdatabilityProfile is { Sources: [{ BackingView.IsWindowed: true }] });

    /// <summary>
    /// Refuses a MERGE through a row-limited or windowed view when it runs:
    /// its matching reads the rows the body yields, which only the DELETE /
    /// UPDATE path pairs back to base rows.
    /// </summary>
    private static void RejectRowSelectiveMergeTarget(ParserContext context, View view, MultiPartName writtenName)
    {
        if (view is { IsRowLimited: true } or { IsWindowed: true } && !context.Batch.IsSkipping)
            throw RowSelectiveViewWriteNotModeled(writtenName.ToString());
    }

    /// <summary>
    /// The rows a windowed or row-limited view or CTE yields, keyed by the base
    /// row each came from, for a DELETE / UPDATE through it: real writes to
    /// exactly those rows and reads the derived columns (a <c>ROW_NUMBER()</c>'s
    /// <c>rn</c>) off them (probed 2026-09-25 against SQL Server 2025). Null
    /// when the target is neither, or the write is positioned.
    /// </summary>
    /// <remarks>
    /// The body runs once. A windowed body's rows arrive in the base heap's
    /// order — the window stage places each result back on its input row — so
    /// they pair, in order, with the base rows the view's filter admits. A
    /// row-limited body yields a subset in its own order, so each of its rows
    /// is matched to an unclaimed base row whose direct columns agree; when two
    /// candidates differ in a column the body didn't project, which one the
    /// limit chose can't be told from its output, and the write refuses rather
    /// than guess.
    /// </remarks>
    private static Dictionary<(int Page, int Slot), SqlValue[]>? MaterializeRowSelectiveViewRows(ParserContext context, View? view, HeapTable table, bool positioned)
    {
        if (view is not ({ IsWindowed: true } or { IsRowLimited: true }) || positioned || context.Batch.IsSkipping)
            return null;
        var body = view.UnstoredBody ?? context.Connection.Simulation.ParseViewBodyPlan(context.Batch, view);
        var outputRows = body.Execute(context.Batch, null).RowValues.ToList();
        var visible = new List<((int Page, int Slot) Address, SqlValue[] Values)>();
        foreach (var (page, slot, bytes) in table.Heap.EnumerateRowsWithAddress())
        {
            var values = DecodeFullRow(table, bytes);
            EvaluateComputedColumns(table, values, context.Batch);
            if (view.VisibilityCheck is not { } isVisible || isVisible(values, context.Batch))
                visible.Add(((page, slot), values));
        }

        var rows = new Dictionary<(int Page, int Slot), SqlValue[]>();
        if (!view.IsRowLimited)
        {
            if (visible.Count != outputRows.Count)
                throw RowSelectiveViewWriteNotModeled(view.Name);
            for (var i = 0; i < visible.Count; i++)
            {
                if (!DirectColumnsAgree(view, outputRows[i], visible[i].Values))
                    throw RowSelectiveViewWriteNotModeled(view.Name);
                rows[visible[i].Address] = outputRows[i];
            }
            return rows;
        }

        var claimed = new bool[visible.Count];
        foreach (var output in outputRows)
        {
            var match = -1;
            for (var i = 0; i < visible.Count; i++)
            {
                if (claimed[i] || !DirectColumnsAgree(view, output, visible[i].Values))
                    continue;
                if (match < 0)
                    match = i;
                else if (!IdenticalRows(visible[match].Values, visible[i].Values))
                    throw RowSelectiveViewWriteNotModeled(view.Name);
            }
            if (match < 0)
                throw RowSelectiveViewWriteNotModeled(view.Name);
            claimed[match] = true;
            rows[visible[match].Address] = output;
        }
        return rows;
    }

    /// <summary>Two base rows equal in every column, strings compared ordinally.</summary>
    private static bool IdenticalRows(SqlValue[] left, SqlValue[] right)
    {
        for (var k = 0; k < left.Length; k++)
        {
            if (left[k].IsNull != right[k].IsNull)
                return false;
            if (left[k].IsNull)
                continue;
            if (SqlType.IsStringCategory(left[k].Type)
                ? !string.Equals(left[k].AsString, right[k].AsString, StringComparison.Ordinal)
                : !left[k].Equals(right[k]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool DirectColumnsAgree(View view, SqlValue[] viewRow, SqlValue[] baseRow)
    {
        for (var v = 0; v < view.BaseColumnOrdinals.Length; v++)
        {
            if (view.BaseColumnOrdinals[v] is var ordinal and >= 0
                && (viewRow[v].IsNull != baseRow[ordinal].IsNull || (!viewRow[v].IsNull && !viewRow[v].Equals(baseRow[ordinal]))))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The write real makes through a body whose <c>TOP</c> / <c>OFFSET</c> or
    /// window function picks its rows, which the simulator refuses rather than
    /// write to every row the body reads.
    /// </summary>
    private static NotSupportedException RowSelectiveViewWriteNotModeled(string viewLabel) =>
        new($"DML through '{viewLabel}' isn't modeled for this shape: its body selects rows with TOP / OFFSET or computes over them with a window function, and SQL Server writes only to the rows that body yields.");

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
    /// Resolves a DML target named by one of the statement's own CTEs as an
    /// unstored view over the CTE's body: real writes through a CTE exactly as
    /// through a view with that body — a plain single-table projection passes
    /// through to the table, a derived column is Msg 4406 and an aggregate body
    /// Msg 4403 (probed 2026-09-25 against SQL Server 2025). A CTE reading
    /// several sources doesn't take the join-view path, which re-parses a
    /// stored view's text.
    /// </summary>
    private static bool TryResolveCteTarget(ParserContext context, MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out View? view)
    {
        view = null;
        if (name.Count != 1 || context.CteBindings is not { } bindings || !bindings.TryGetValue(name.Leaf, out var binding) || binding.Plan is not { } body)
            return false;
        if (binding.DmlTarget is null)
        {
            var collation = context.CurrentDatabase.Collation;
            var (baseTable, baseColumnOrdinals, rejection, visibilityCheck, checkOptionCheck, _) = AnalyzeViewUpdatability(collation, body, withCheckOption: false);
            binding.DmlTarget = new View(
                context.CurrentDatabase.Schemas[Database.DefaultSchemaName],
                binding.Name,
                objectId: 0,
                ComputeViewOutputColumns(collation, body, [.. binding.ColumnNames], binding.Name),
                bodyText: string.Empty,
                withCheckOption: false,
                isSchemaBound: false,
                createDate: default,
                baseTable,
                baseColumnOrdinals,
                rejection,
                visibilityCheck,
                checkOptionCheck,
                isJoinUpdatable: false)
            {
                DerivedOutputColumns = baseTable is null && rejection != ViewUpdatabilityRejection.MultipleSources ? DerivedOutputColumnsOf(body) : null,
                IsRowLimited = IsRowLimitedBody(body),
                IsWindowed = IsWindowedBody(body),
                UnstoredBody = body,
            };
        }
        view = binding.DmlTarget;
        return true;
    }
}
