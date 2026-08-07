using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses a <c>WITH [XMLNAMESPACES (…),] cte_name [(col, …)] AS
    /// (SELECT …) [, …]</c> prefix and registers each binding on
    /// <paramref name="context"/>'s <see cref="ParserContext.CteBindings"/> /
    /// <see cref="ParserContext.XmlNamespaces"/>. The bindings live for the
    /// immediately-following statement only — the statement loop clears
    /// the slots on its next iteration.
    /// </summary>
    /// <remarks>
    /// On entry <see cref="ParserContext.Token"/> is the <c>WITH</c>
    /// keyword. On return it sits on the first token of the dispatched
    /// statement (<c>SELECT</c> / <c>INSERT</c> / <c>UPDATE</c> /
    /// <c>DELETE</c> / <c>MERGE</c>) so the surrounding switch can resume
    /// dispatch.
    /// </remarks>
    /// <summary>
    /// Parses a stored body's query — an optional <c>WITH cte [, …]</c> prefix
    /// followed by the <c>SELECT</c> it scopes to — at statement depth. The
    /// seam every body-parse site shares: <c>CREATE</c> / <c>ALTER VIEW</c> and
    /// each later re-parse of a view's stored text (invocation, indexed-view
    /// materialization, shape analysis, base-table collection), the inline
    /// TVF's <c>RETURN</c> body at both create and invoke, and
    /// <c>DECLARE … CURSOR FOR</c>.
    /// </summary>
    /// <remarks>
    /// A body is its own parse unit — the dispatch loop's WITH handling never
    /// sees it, which is why a leading WITH has to be recognized here. The
    /// bindings register on <paramref name="context"/> exactly as a top-level
    /// prefix would, so the CTE resolves through the same FROM-source lookup;
    /// bodies parsed in a child <see cref="BatchContext"/> get a fresh
    /// <see cref="ParserContext"/> per parse, and the one body parsed on the
    /// caller's own context (CREATE VIEW, which must start its batch) is
    /// followed only by the trailing <c>WITH CHECK OPTION</c> scan before the
    /// statement loop clears the slot.
    /// <para>
    /// Positions where a query may <em>not</em> carry a CTE prefix — a derived
    /// table, a scalar subquery, the <c>RETURN (…)</c> expression of a scalar
    /// UDF — keep calling <see cref="Selection.Parse"/> directly and so keep
    /// rejecting the WITH, matching real.
    /// </para>
    /// </remarks>
    internal static Selection ParseBodyQuery(ParserContext context, bool rejectsNextValueFor = false)
    {
        // A view or function body is one of the constructs real names in
        // Msg 11719, and it refuses the CREATE rather than the later reference
        // (probe-confirmed, with the error attributed to the module).
        var savedRejection = context.NextValueForRejection;
        if (rejectsNextValueFor)
            _ = context.EnterNextValueForScope(NextValueForScope.Nested);
        try
        {
            if (context.Token is ReservedKeyword { Keyword: Keyword.With })
                ParseCteBindings(context);
            return Selection.Parse(context, depth: 0);
        }
        finally
        {
            context.NextValueForRejection = savedRejection;
        }
    }

    private static void ParseCteBindings(ParserContext context)
    {
        var bindings = new Dictionary<string, CteBinding>(StringComparer.OrdinalIgnoreCase);
        context.CteBindings = bindings;

        context.MoveNextRequired();

        // XMLNAMESPACES leads the WITH prefix — alone, or ahead of a
        // comma-separated CTE list. Real accepts it only in first position (one
        // written after a CTE is Msg 102 near 'XMLNAMESPACES'), and treats the
        // word as a keyword there: `WITH XMLNAMESPACES AS (…)` is a syntax
        // error while the delimited `WITH [XMLNAMESPACES] AS (…)` is an
        // ordinary CTE, so only the unquoted spelling enters the clause.
        if (context.Token is UnquotedString word && Collation.Baseline.Equals(word.Value, "XMLNAMESPACES"))
        {
            context.XmlNamespaces = ForXmlNamespaces.Parse(context);
            if (context.Token is not Operator { Character: ',' })
                return;
            context.MoveNextRequired();
        }

        while (true)
        {
            // Past first position the word is still a keyword, so it can't
            // become a CTE name: real reports Msg 102 on it rather than on
            // whatever follows.
            if (context.Token is not Name cteName
                || (context.Token is UnquotedString late && Collation.Baseline.Equals(late.Value, "XMLNAMESPACES")))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            if (bindings.ContainsKey(cteName.Value))
                throw SimulatedSqlException.DuplicateCteName(cteName.Value);

            // Msg 10137 names the first CTE a view body declares, whether or
            // not the body's SELECT reaches it (probe-confirmed: a two-CTE
            // body whose SELECT reads only the second still names the first,
            // and an entirely unreferenced CTE names itself).
            if (context.IndexedViewShapeCollector is { } shapeCollector)
                shapeCollector.CteName ??= cteName.Value;

            string[]? renameList = null;
            context.MoveNextRequired();
            if (context.Token is Operator { Character: '(' })
            {
                renameList = ParseCteColumnRenameList(context);
                context.MoveNextRequired();
            }

            if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            // Register the sentinel before parsing the body. A self-
            // reference in the first branch resolves to a null-Plan
            // binding without IsRecursivePartParse set, raising Msg 252
            // (no top-level UNION ALL with a valid anchor).
            var binding = new CteBinding(cteName.Value, []);
            bindings[cteName.Value] = binding;

            var body = ParseCteBodyRecordingReads(context, binding, renameList);

            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (body.HasOrderBy && !body.HasTopOrOffsetOrFetch)
                throw SimulatedSqlException.OrderByInvalidInCte();

            string[] columnNames;
            if (renameList is not null)
            {
                if (renameList.Length < body.Schema.Length)
                    throw SimulatedSqlException.HasMoreColumnsThanColumnList(cteName.Value);
                if (renameList.Length > body.Schema.Length)
                    throw SimulatedSqlException.HasFewerColumnsThanColumnList(cteName.Value);
                columnNames = renameList;
            }
            else
            {
                columnNames = body.ColumnNames;
            }

            // The same binding instance stays in the dictionary so
            // self-reference FromSources built during the recursive parse
            // (which captured the binding by reference) keep reading from
            // the same MaxRecursion / CurrentIterationRows slots. The
            // rename list updates the column names in-place.
            binding.Plan = body;
            binding.ColumnNames = columnNames;

            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Parses a CTE body under a securable sink of its own and attaches the
    /// result to the body plan. A CTE body reaches
    /// <see cref="Selection.ParseIntersectChain"/> directly rather than through
    /// the query-expression entry that creates the sink, so without this its
    /// reads are recorded nowhere; the referencing FROM source folds the
    /// attached list into the statement's, where the ordinary execution-time
    /// SELECT check picks it up.
    /// </summary>
    private static Selection ParseCteBodyRecordingReads(ParserContext context, CteBinding binding, string[]? renameList)
    {
        var outerSecurables = context.SecurableSink;
        var outerReadColumns = context.ReadColumnSink;
        context.SecurableSink = [];
        context.ReadColumnSink = [];
        try
        {
            // Real refuses NEXT VALUE FOR inside a common table expression by
            // name (Msg 11719, probe-confirmed) — over the body's whole parse,
            // not merely its clauses.
            var savedRejection = context.EnterNextValueForScope(NextValueForScope.Nested);
            Selection body;
            try
            {
                body = ParseCteBody(context, binding, renameList);
            }
            finally
            {
                context.NextValueForRejection = savedRejection;
            }

            if (context.SecurableSink is { Count: > 0 } bodySecurables)
                body.ReferencedSecurables = bodySecurables;
            if (context.ReadColumnSink is { Count: > 0 } bodyReadColumns)
                body.ReadColumnsByObject = bodyReadColumns;
            return body;
        }
        finally
        {
            context.SecurableSink = outerSecurables;
            context.ReadColumnSink = outerReadColumns;
        }
    }

    /// <summary>
    /// Parses a CTE body as a sequence of UNION / UNION ALL branches,
    /// detecting recursion via per-branch self-reference tracking. Each
    /// branch parses through <see cref="Selection.ParseIntersectChain"/> so
    /// nested INTERSECT (the higher-precedence set op) composes
    /// transparently inside a single branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anchor branches (no self-reference) precede recursive branches (one
    /// self-reference each); ordering is enforced via Msg 247. The first
    /// branch's schema becomes the binding's reference schema; subsequent
    /// recursive branches must match column-for-column (Msg 240).
    /// </para>
    /// <para>
    /// A recursive member sees the CTE's columns under the names the
    /// <c>WITH cte (a, b, …)</c> list declares, not the names the anchor's own
    /// projection carries — which is what lets AdventureWorks'
    /// <c>uspGetBillOfMaterials</c> family write <c>[RecursionLevel] + 1</c>
    /// against an anchor whose matching column is the unaliased literal
    /// <c>0</c>. So the list installs before the recursive branches parse.
    /// Real still reports the arity mismatch (Msg 8158 / 8159) only after the
    /// whole body binds — a recursive member naming a declared column resolves
    /// against the list whatever its length — so a list that disagrees with the
    /// anchor is resized here and left for the caller's check to reject.
    /// </para>
    /// </remarks>
    private static Selection ParseCteBody(ParserContext context, CteBinding binding, string[]? renameList)
    {
        var firstBranch = Selection.ParseIntersectChain(context, depth: 1, outerTypeResolver: null, isFirstBranch: true, namesOwnCollation: false);

        var branches = new List<(Selection plan, bool selfRef, SetOpKind op)>
        {
            (firstBranch, false, SetOpKind.UnionAll),
        };

        binding.Schema = firstBranch.Schema;
        binding.ColumnNames = renameList is null
            ? firstBranch.ColumnNames
            : ResizeCteColumnNames(renameList, firstBranch.ColumnNames);
        binding.IsRecursivePartParse = true;

        try
        {
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

                binding.SelfReferenceCountInCurrentBranch = 0;
                context.RecursiveBranchConstructs = default;
                var branch = Selection.ParseIntersectChain(context, depth: 1, outerTypeResolver: null, isFirstBranch: false, namesOwnCollation: false);
                var selfRefCount = binding.SelfReferenceCountInCurrentBranch;
                if (selfRefCount > 1)
                    throw SimulatedSqlException.RecursiveCteMultipleReferences(binding.Name);
                if (selfRefCount > 0)
                    RejectRecursiveMemberConstructs(context.RecursiveBranchConstructs, binding.Name);

                branches.Add((branch, selfRefCount > 0, kind));
            }
        }
        finally
        {
            binding.IsRecursivePartParse = false;
            binding.SelfReferenceCountInCurrentBranch = 0;
        }

        var hasRecursive = false;
        for (var i = 0; i < branches.Count; i++)
        {
            if (branches[i].selfRef) { hasRecursive = true; break; }
        }

        if (!hasRecursive)
        {
            // Non-recursive CTE — fold branches via the standard set-op
            // combiner so type promotion matches a regular UNION ALL chain.
            var combined = branches[0].plan;
            for (var i = 1; i < branches.Count; i++)
                combined = Selection.CombineSetOps(combined, branches[i].plan, branches[i].op, namesOwnCollation: false);
            return combined;
        }

        // Recursive CTE — every operator separating branches must be
        // UNION ALL (Msg 252); type equality strict per column (Msg 240);
        // anchor branches must precede recursive branches (Msg 247).
        for (var i = 1; i < branches.Count; i++)
        {
            if (branches[i].op != SetOpKind.UnionAll)
                throw SimulatedSqlException.RecursiveCteMissingUnionAll(binding.Name);
        }

        var anchors = new List<Selection>();
        var recursives = new List<Selection>();
        var seenRecursive = false;
        foreach (var (plan, selfRef, _) in branches)
        {
            if (selfRef)
            {
                seenRecursive = true;
                recursives.Add(plan);
            }
            else
            {
                if (seenRecursive)
                    throw SimulatedSqlException.AnchorAfterRecursive(binding.Name);
                anchors.Add(plan);
            }
        }

        // Strict type-equality between anchor and every recursive branch
        // (Msg 240) — recursive CTEs don't get the Promote-style widening
        // that regular UNION ALL applies. The first anchor branch's schema
        // is the reference; every other branch must match column-for-column.
        var anchorSchema = anchors[0].Schema;
        var anchorColumnNames = anchors[0].ColumnNames;
        for (var bi = 1; bi < anchors.Count; bi++)
            ValidateRecursiveBranchTypes(anchors[bi], anchorSchema, anchorColumnNames, binding.Name);
        for (var bi = 0; bi < recursives.Count; bi++)
            ValidateRecursiveBranchTypes(recursives[bi], anchorSchema, anchorColumnNames, binding.Name);

        // A recursive-CTE plan mutates its CteBinding at EXECUTION time
        // (CurrentIterationRows rebinds between iterations), so a plan-cached
        // copy replayed by two commands concurrently would cross-feed
        // iteration rowsets. Disqualify the batch from plan-cache promotion —
        // each execution re-parses and owns a fresh binding. (A FROM-less
        // anchor already disqualified via BuildSynthesizedSqlRow; this covers
        // the FROM-ful-anchor shape.)
        context.Batch.HasSessionScopedReference = true;

        return Selection.FromRecursiveCte([.. anchors], [.. recursives], binding);
    }

    /// <summary>
    /// The declared column names a self-reference exposes, sized to the
    /// anchor's own column count. Returns <paramref name="renameList"/> itself
    /// when the two already agree, which is every well-formed CTE; a list that
    /// disagrees is truncated or padded from <paramref name="anchorNames"/> so
    /// the FromSource's names and columns stay the same length until the
    /// caller's Msg 8158 / 8159 check rejects the statement.
    /// </summary>
    private static string[] ResizeCteColumnNames(string[] renameList, string[] anchorNames)
    {
        if (renameList.Length == anchorNames.Length)
            return renameList;
        var sized = new string[anchorNames.Length];
        for (var i = 0; i < sized.Length; i++)
            sized[i] = i < renameList.Length ? renameList[i] : anchorNames[i];
        return sized;
    }

    private static void ValidateRecursiveBranchTypes(Selection branch, Storage.SqlType[] anchorSchema, string[] anchorColumnNames, string cteName)
    {
        if (branch.Schema.Length != anchorSchema.Length)
            throw SimulatedSqlException.SetOpUnequalColumnCount();
        for (var ci = 0; ci < anchorSchema.Length; ci++)
        {
            if (branch.Schema[ci] != anchorSchema[ci])
                throw SimulatedSqlException.RecursiveCteTypeMismatch(anchorColumnNames[ci], cteName);
        }
    }

    /// <summary>
    /// Parses the optional <c>(col1, col2, …)</c> column-rename list that
    /// follows a CTE name. Enters with <see cref="ParserContext.Token"/>
    /// on the <c>(</c>; on return <see cref="ParserContext.Token"/> sits
    /// on the closing <c>)</c>.
    /// </summary>
    private static string[] ParseCteColumnRenameList(ParserContext context)
    {
        var names = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name columnName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(columnName.Value);

            context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
                return [.. names];
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
    /// <summary>
    /// Raises the error SQL Server gives for a construct the recursive member
    /// of a recursive CTE may not contain. Probe-confirmed 2026-07-31,
    /// including that the restriction reaches into the member's nested
    /// subqueries and derived tables rather than stopping at its own SELECT.
    /// </summary>
    private static void RejectRecursiveMemberConstructs(RecursiveMemberConstructs seen, string cteName)
    {
        if (seen.Distinct)
            throw SimulatedSqlException.RecursiveCteDistinctNotAllowed(cteName);
        if (seen.TopOrOffset)
            throw SimulatedSqlException.RecursiveCteTopNotAllowed(cteName);
        if (seen.OuterJoin)
            throw SimulatedSqlException.RecursiveCteOuterJoinNotAllowed(cteName);
        if (seen.GroupingOrAggregate)
            throw SimulatedSqlException.RecursiveCteGroupingNotAllowed(cteName);
    }
}
