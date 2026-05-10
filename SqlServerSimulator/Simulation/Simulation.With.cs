using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses a <c>WITH cte_name [(col, …)] AS (SELECT …) [, …]</c> prefix
    /// and registers each binding on <paramref name="context"/>'s
    /// <see cref="ParserContext.CteBindings"/>. The bindings live for the
    /// immediately-following statement only — the statement loop clears
    /// the slot on its next iteration.
    /// </summary>
    /// <remarks>
    /// On entry <see cref="ParserContext.Token"/> is the <c>WITH</c>
    /// keyword. On return it sits on the first token of the dispatched
    /// statement (<c>SELECT</c> / <c>INSERT</c> / <c>UPDATE</c> /
    /// <c>DELETE</c> / <c>MERGE</c>) so the surrounding switch can resume
    /// dispatch.
    /// </remarks>
    private static void ParseCteBindings(ParserContext context)
    {
        var bindings = new Dictionary<string, CteBinding>(StringComparer.OrdinalIgnoreCase);
        context.CteBindings = bindings;

        while (true)
        {
            if (context.GetNextRequired() is not Name cteName)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (bindings.ContainsKey(cteName.Value))
                throw SimulatedSqlException.DuplicateCteName(cteName.Value);

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

            var body = ParseCteBody(context, binding);

            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (body.HasOrderBy && !body.HasTopOrOffsetOrFetch)
                throw SimulatedSqlException.OrderByInvalidInCte();

            string[] columnNames;
            if (renameList is not null)
            {
                if (renameList.Length < body.Schema.Length)
                    throw SimulatedSqlException.CteHasMoreColumnsThanList(cteName.Value);
                if (renameList.Length > body.Schema.Length)
                    throw SimulatedSqlException.CteHasFewerColumnsThanList(cteName.Value);
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
    /// Anchor branches (no self-reference) precede recursive branches (one
    /// self-reference each); ordering is enforced via Msg 247. The first
    /// branch's schema becomes the binding's reference schema; subsequent
    /// recursive branches must match column-for-column (Msg 240).
    /// </remarks>
    private static Selection ParseCteBody(ParserContext context, CteBinding binding)
    {
        var firstBranch = Selection.ParseIntersectChain(context, depth: 1, outerTypeResolver: null, isFirstBranch: true);

        var branches = new List<(Selection plan, bool selfRef, SetOpKind op)>
        {
            (firstBranch, false, SetOpKind.UnionAll),
        };

        binding.Schema = firstBranch.Schema;
        binding.ColumnNames = firstBranch.ColumnNames;
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
                var branch = Selection.ParseIntersectChain(context, depth: 1, outerTypeResolver: null, isFirstBranch: false);
                var selfRefCount = binding.SelfReferenceCountInCurrentBranch;
                if (selfRefCount > 1)
                    throw SimulatedSqlException.RecursiveCteMultipleReferences(binding.Name);

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
                combined = Selection.CombineSetOps(combined, branches[i].plan, branches[i].op);
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

        return Selection.FromRecursiveCte([.. anchors], [.. recursives], binding);
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
}
