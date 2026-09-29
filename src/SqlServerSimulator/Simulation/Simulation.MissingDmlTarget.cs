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

    /// <summary>A token that neither ends a statement nor starts the next is Msg 102 (156 for a reserved word).</summary>
    private static void RejectStrayToken(ParserContext context)
    {
        if (!IsStatementBoundary(context.Token))
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// The compile pass's read of an INSERT whose target didn't resolve: the
    /// column list, then the source — <c>VALUES</c> tuples, a query, or
    /// <c>DEFAULT VALUES</c>. An <c>EXEC</c> source is left unread.
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
                _ = ParseValuesTuples(context, allowDefault: true);
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
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        RejectStrayToken(context);
    }
}
