using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    // Real parses a whole batch before binding any of it, so a syntax error
    // anywhere past a DML statement's target outranks that target's Msg 208
    // (probed 2026-09-29 against SQL Server 2025). The compile pass reads the
    // rest of such a statement against placeholder columns, which type as
    // int and accept any name, so the statement's own grammar is judged
    // before the missing object is; the run pass raises the Msg 208.

    /// <summary>
    /// Steps over a DML statement's <c>OUTPUT</c> clause without binding it,
    /// to the first top-level keyword that ends it — <c>FROM</c>, <c>WHERE</c>,
    /// <c>OPTION</c>, an INSERT's source, or the statement end. A statement
    /// whose target is missing has no table its columns could bind to.
    /// </summary>
    private static void SkipOutputClause(ParserContext context)
    {
        var depth = 0;
        while (context.Token is { } token)
        {
            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: ';' }:
                    return;
                case ReservedKeyword { Keyword: Keyword.From or Keyword.Where or Keyword.Option or Keyword.Values or Keyword.Select or Keyword.Default or Keyword.Exec or Keyword.Execute } when depth == 0:
                    return;
            }
            context.MoveNextOptional();
        }
    }

    /// <summary>
    /// The compile pass's read of an UPDATE or DELETE whose target didn't
    /// resolve and that has no <c>FROM</c>: its <c>WHERE</c> parses over
    /// placeholder columns and its <c>OPTION</c> list is stepped over, so a
    /// syntax error there — or a stray token after — is Msg 102 ahead of the
    /// missing object.
    /// </summary>
    private static void ParseMissingTargetTail(ParserContext context)
    {
        if (!context.Batch.IsSkipping)
            return;
        context.Batch.CurrentStatement.BindsDeferredSource = true;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            if (!SkipCurrentOf(context))
                _ = Selection.ParseAndBindPredicate(context, static _ => SqlType.Int32);
        }
        SkipOptionClause(context);
        RejectStrayToken(context);
    }

    /// <summary>
    /// The joined-form counterpart of <see cref="ParseMissingTargetTail"/>,
    /// for an UPDATE or DELETE whose leading name is no table and names no
    /// FROM source that reads one: the compile pass reads the <c>WHERE</c> over
    /// the FROM sources plus a placeholder standing for the target, and the
    /// caller returns without going on. False when the target binds, leaving
    /// the ordinary path (and its errors) alone.
    /// </summary>
    private static bool ReadJoinedTailPastMissingTarget(
        ParserContext context, List<FromSource> sources, List<JoinSpec> joins, MultiPartName leadingIdent, HeapTable? leadingTable)
    {
        if (!context.Batch.IsSkipping || leadingTable is not null)
            return false;
        var found = FindMutationTargetIndex(context.Batch.CurrentDatabase.Collation, sources, leadingIdent.Leaf, leadingTable);
        if (found >= 0 && !sources[found].IsPlaceholder)
            return false;
        if (found < 0)
        {
            joins.Add(new JoinSpec(JoinKind.Cross, onPredicate: null));
            sources.Add(FromSource.DeferredPlaceholder(leadingIdent.Leaf));
        }
        context.Batch.CurrentStatement.BindsDeferredSource = true;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            if (!SkipCurrentOf(context))
            {
                FromSource[] scope = [.. sources];
                _ = Selection.ParseAndBindPredicate(context, Selection.ColumnTypeResolverFor(scope), scope, [.. joins]);
            }
        }
        SkipOptionClause(context);
        RejectStrayToken(context);
        return true;
    }

    /// <summary>Steps over a statement's <c>OPTION ( … )</c> query-hint list, unbound.</summary>
    private static void SkipOptionClause(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.Option })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var depth = 0;
            while (true)
            {
                if (context.Token is Operator { Character: '(' })
                {
                    depth++;
                }
                else if (context.Token is Operator { Character: ')' } && --depth == 0)
                {
                    context.MoveNextOptional();
                    break;
                }
                context.MoveNextRequired();
            }
        }
    }

    /// <summary>
    /// Steps over a <c>WHERE CURRENT OF [GLOBAL] cursor</c> tail, whose cursor
    /// binds at run time; false, having read nothing, when the token isn't
    /// <c>CURRENT</c>.
    /// </summary>
    private static bool SkipCurrentOf(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Current })
            return false;
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Of })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        _ = ReadCursorReference(context);
        return true;
    }

    /// <summary>
    /// A token that neither ends a statement nor starts the next is Msg 102
    /// (156 for a reserved word); otherwise the statement was read to its end.
    /// </summary>
    private static void RejectStrayToken(ParserContext context)
    {
        if (!EndsStatement(context.Token))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.Token is Operator { Character: '(' } paren)
            context.StatementEndedOnParen = paren.StartIndex;
        context.Batch.CurrentStatement.DeferredReadToEnd = true;
    }

    /// <summary>
    /// The compile pass's read of an INSERT whose target didn't resolve: the
    /// column list, then the source — <c>VALUES</c> tuples, a query,
    /// <c>DEFAULT VALUES</c>, or an <c>EXEC</c> with its argument list, which
    /// runs nothing in the compile pass.
    /// </summary>
    private static void ParseMissingInsertTail(ParserContext context)
    {
        if (!context.Batch.IsSkipping)
            return;
        if (context.Token is Operator { Character: '(' })
        {
            while (true)
            {
                if (context.GetNextRequired() is not StringToken)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var separator = context.GetNextRequired();
                if (separator is Operator { Character: ')' })
                    break;
                if (separator is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            SkipOutputClause(context);
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Values }:
                // The tuples read no column of the target, so they bind as the
                // batch compiles: a column name there is Msg 207, an illegal
                // CAST Msg 529 (probed 2026-10-01 against SQL Server 2025).
                foreach (var tuple in ParseValuesTuples(context, allowDefault: true))
                {
                    foreach (var cell in tuple)
                    {
                        if (cell is not Parser.Expressions.DefaultValueExpression)
                            _ = cell.GetSqlType(context.Batch, static name => throw SimulatedSqlException.UnboundColumnReference(name));
                    }
                }
                break;
            case ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: '(' }:
                _ = Selection.Parse(context, new QueryScope(QueryPosition.InsertSource, null));
                break;
            case ReservedKeyword { Keyword: Keyword.Default }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Values })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
                break;
            case ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute }:
                foreach (var _ in context.Batch.Connection.Simulation.ParseExec(context.Batch, insertExecSource: true))
                {
                    // Skip mode yields nothing; the enumeration drives the parse.
                }
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        SkipOptionClause(context);
        RejectStrayToken(context);
    }

    /// <summary>
    /// The read of a MERGE whose target didn't resolve, from the target name's
    /// last token. The <c>USING</c> source binds as it always does, so a
    /// missing source is the Msg 208 real reports ahead of the target's (probed
    /// 2026-09-30 against SQL Server 2025). In the compile pass the rest — the
    /// target's hints and alias, a source that doesn't resolve either, the
    /// <c>ON</c> predicate, every <c>WHEN</c> clause's condition and action,
    /// <c>OUTPUT</c> and <c>OPTION</c> — reads over placeholder columns, and the
    /// statement's closing <c>;</c> is required, so a syntax error anywhere in
    /// it outranks the missing target and a dead branch parses to its end.
    /// </summary>
    private static void ParseMissingMergeTail(ParserContext context, MultiPartName targetName)
    {
        context.MoveNextRequired();
        if (!BatchContext.IsTableVariableName(targetName.Leaf))
            Selection.ValidateDmlTargetHints(Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false), targetName.ToString(), "MERGE");
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            context.MoveNextRequired();
        if (context.Token is Name and not UnquotedString { ContextualKeyword: ContextualKeyword.Using })
            context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Using })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (!context.Batch.IsSkipping || !SkipUnresolvedMergeSource(context))
            _ = ParseMergeSource(context);
        if (!context.Batch.IsSkipping)
            return;
        context.Batch.CurrentStatement.BindsDeferredSource = true;

        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        _ = Selection.ParseAndBindPredicate(context, static _ => SqlType.Int32);

        var clauses = 0;
        while (context.Token is ReservedKeyword { Keyword: Keyword.When })
        {
            clauses++;
            if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Not })
                context.MoveNextRequired();
            if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Matched })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.By })
            {
                if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Source or ContextualKeyword.Target })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
            if (context.Token is ReservedKeyword { Keyword: Keyword.And })
            {
                context.MoveNextRequired();
                _ = Selection.ParseAndBindPredicate(context, static _ => SqlType.Int32);
            }
            if (context.Token is not ReservedKeyword { Keyword: Keyword.Then })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.GetNextRequired())
            {
                case ReservedKeyword { Keyword: Keyword.Delete }:
                    context.MoveNextOptional();
                    break;
                case ReservedKeyword { Keyword: Keyword.Update }:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    do
                    {
                        context.MoveNextRequired();
                        _ = BatchContext.ParseObjectName(context);
                        // A column's mutator method is the whole clause.
                        if (context.GetNextRequired() is Operator { Character: '(' })
                        {
                            do
                            {
                                context.MoveNextRequired();
                                ParseMissingMergeValue(context);
                            }
                            while (context.Token is Operator { Character: ',' });
                            if (context.Token is not Operator { Character: ')' })
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            context.MoveNextOptional();
                            continue;
                        }
                        if (context.Token is not Operator { Character: '=' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Default })
                            context.MoveNextOptional();
                        else
                            ParseMissingMergeValue(context);
                    }
                    while (context.Token is Operator { Character: ',' });
                    break;
                case ReservedKeyword { Keyword: Keyword.Insert }:
                    if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Default })
                    {
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Values })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextOptional();
                        break;
                    }
                    if (context.Token is Operator { Character: '(' })
                    {
                        do
                        {
                            if (context.GetNextRequired() is not StringToken)
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                        }
                        while (context.GetNextRequired() is Operator { Character: ',' });
                        if (context.Token is not Operator { Character: ')' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextRequired();
                    }
                    if (context.Token is not ReservedKeyword { Keyword: Keyword.Values } || context.GetNextRequired() is not Operator { Character: '(' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    do
                    {
                        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Default })
                            context.MoveNextRequired();
                        else
                            ParseMissingMergeValue(context);
                    }
                    while (context.Token is Operator { Character: ',' });
                    if (context.Token is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
        if (clauses == 0)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            SkipOutputClause(context);
        SkipOptionClause(context);
        if (context.Token is not Operator { Character: ';' })
            throw EndsStatement(context.Token) ? SimulatedSqlException.MergeMustBeTerminated() : SimulatedSqlException.SyntaxErrorNear(context);
        context.Batch.CurrentStatement.DeferredReadToEnd = true;
    }

    /// <summary>
    /// Steps over a MERGE's bare-name <c>USING</c> source that names nothing
    /// the statement can read, with its alias and hints, in the compile pass;
    /// false, the cursor on <c>USING</c>, for any other source.
    /// </summary>
    private static bool SkipUnresolvedMergeSource(ParserContext context)
    {
        var atUsing = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' })
        {
            var name = BatchContext.ParseObjectName(context, acceptTableVariable: true);
            var batch = context.Batch;
            var resolves = (name.Count == 1 && context.CteBindings is { } ctes && ctes.ContainsKey(name.Leaf))
                || name.Count == 4
                || batch.TryResolveView(name, out _)
                || batch.TryResolveTable(name, out _);
            if (!resolves)
            {
                _ = Selection.ConsumeOptionalAlias(context);
                _ = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: true, commitOnLegacyParen: true);
                return true;
            }
        }
        context.RestoreCheckpoint(atUsing);
        return false;
    }

    /// <summary>A value in a missing MERGE target's action, read over placeholder columns.</summary>
    private static void ParseMissingMergeValue(ParserContext context)
    {
        var aggregates = new List<Parser.Expressions.AggregateExpression>();
        using (ParserScope.Enter(ref context.OuterTypeResolver, static _ => SqlType.Int32))
        using (ParserScope.Enter(ref context.AggregateCollector, aggregates))
        {
            _ = Expression.Parse(context);
        }
    }
}
