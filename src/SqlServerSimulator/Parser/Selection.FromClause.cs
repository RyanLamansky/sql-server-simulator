using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// FROM-clause parsing: sources, joins and APPLY, derived tables and VALUES constructors, column-alias lists and aliases.
internal sealed partial class Selection
{
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
                    // A statement keyword outside parentheses starts the next
                    // statement of a batch written without semicolons, whose
                    // FROM isn't this one's.
                    case ReservedKeyword { Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge or Keyword.Declare or Keyword.Set or Keyword.If or Keyword.While or Keyword.Exec or Keyword.Execute or Keyword.Print } when parenDepth == 0:
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

    /// <summary>
    /// An alias-form <c>UPDATE</c>'s <c>FROM</c> clause, parsed ahead of the
    /// <c>SET</c> list so a subquery there binds against the statement's
    /// sources, and the checkpoint just past it where the statement resumes.
    /// </summary>
    internal sealed class PreParsedFrom(List<FromSource> sources, List<JoinSpec> joins, ParserContext.Checkpoint after)
    {
        public readonly List<FromSource> Sources = sources;
        public readonly List<JoinSpec> Joins = joins;
        public readonly ParserContext.Checkpoint After = after;
    }

    /// <summary>
    /// Parses an alias-form <c>UPDATE</c>'s own <c>FROM</c> clause ahead of its
    /// <c>SET</c> list, entered and left with the cursor on the <c>SET</c>
    /// keyword, so a <c>SET</c> subquery reading the target's alias binds
    /// (probed 2026-09-28 against SQL Server 2025: EF Core's <c>UPDATE [b] SET
    /// [b].[n] = (SELECT COUNT(*) FROM [p] WHERE [b].[Id] = [p].[BlogId]) FROM
    /// [Blogs] AS [b]</c>) — or, <paramref name="fromCursor"/>, a
    /// <c>DELETE</c>'s, entered and left on the token after its target, ahead
    /// of the <c>OUTPUT</c> clause binding against the table it names.
    /// Speculative in the way the <c>SELECT</c> pre-pass is: null when there is
    /// no such clause or it doesn't parse on its own, and the statement then
    /// parses it in place.
    /// </summary>
    internal static PreParsedFrom? PreParseMutationFrom(ParserContext context, bool fromCursor = false)
    {
        var atSet = context.SaveCheckpoint();
        if ((!fromCursor && !context.MoveNext()) || FindOwnFromClause(context) is not { } fromCheckpoint)
        {
            context.RestoreCheckpoint(atSet);
            return null;
        }
        context.RestoreCheckpoint(fromCheckpoint);
        context.Batch.BindErrors?.EnterClause(context.Token, BindClause.From);
        var sources = new List<FromSource>();
        var joins = new List<JoinSpec>();
        using var mutationFrom = ParserScope.Enter(ref context.AllowNextValueForInFromClause, true);
        try
        {
            ParseSourcesAndJoins(context, QueryScope.Statement, sources, joins);
            return new PreParsedFrom(sources, joins, context.SaveCheckpoint());
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            context.RestoreCheckpoint(atSet);
        }
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
    private static void ParseFromSourceAndJoins(
        ParserContext context,
        QueryScope scope,
        List<FromSource> sources,
        List<JoinSpec> joins,
        FromClause fromClause,
        bool allowOrderBy)
    {
        var remoteSourcesBefore = context.RemoteSourcesParsed;
        ParseSourcesAndJoins(context, scope, sources, joins);
        fromClause.ReadsRemoteSource = context.RemoteSourcesParsed > remoteSourcesBefore;

        // Now register the multi-source type resolver and parse WHERE / etc.
        ConsumeWhereOrderByWithOuterScope(context, fromClause, [.. sources], [.. joins], allowOrderBy, scope);
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
        QueryScope scope,
        List<FromSource> sources,
        List<JoinSpec> joins)
    {
        // A FROM clause at any nesting depth is what makes a function body's
        // rejected SELECT real's Msg 444 state 2 rather than state 3.
        FunctionBodyShape.NoteRowsetRead(context);
        using var clause = ParserScope.Enter(ref context.InliningClause, InliningClause.Other);
        // Every column reference a non-APPLY source's own arguments name, kept
        // until the whole FROM is parsed — a source may name a sibling written
        // after it, so the check can't run per source. Local to this FROM, so a
        // nested one validates against its own sources.
        var siblingCandidates = new List<Reference>();
        ParseExplicitJoinChain(context, scope, sources, joins, siblingCandidates);

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
            joins.Add(new JoinSpec(JoinKind.Cross, onPredicate: null) { IsComma = true });
            ParseExplicitJoinChain(context, scope, sources, joins, siblingCandidates);
        }

        RejectSiblingReferences(siblingCandidates, sources, scope.OuterTypeResolver);
    }

    /// <summary>
    /// Appends <paramref name="added"/> to the FROM clause's sources, refusing
    /// it first when its exposed name repeats an earlier source's — an
    /// unaliased table exposes its name's last part, whatever schema or
    /// database the name carries; an alias, or a CTE's own name, is a
    /// correlation name; a source with neither (an unaliased rowset function)
    /// exposes nothing. Checked as each source joins, so it outranks an error
    /// in that source's own ON predicate (probed 2026-09-26 against SQL Server
    /// 2025).
    /// </summary>
    private static void AddSource(ParserContext context, List<FromSource> sources, FromSource added)
    {
        if (added.Qualifier is { } exposed)
        {
            var collation = context.Batch.CurrentDatabase.Collation;
            var addedTable = ExposedTableName(collation, added);
            foreach (var earlier in sources)
            {
                if (earlier.Qualifier is null || !collation.Equals(earlier.Qualifier, exposed))
                    continue;
                var earlierTable = ExposedTableName(collation, earlier);
                var collision = (addedTable, earlierTable) switch
                {
                    (null, null) => SimulatedSqlException.CorrelationNameRepeated(exposed),
                    (null, { } table) => SimulatedSqlException.CorrelationNameMatchesTable(exposed, table),
                    ({ } table, null) => SimulatedSqlException.CorrelationNameMatchesTable(earlier.Qualifier, table),
                    ({ } later, { } first) => SimulatedSqlException.SameExposedNames(later, first),
                };
                // Read for a whole bind error report, the statement binds on so
                // the ON predicates ahead of the collision report too.
                if (context.Batch.BindErrors is not { } report || !report.Covers(context.Token))
                    throw collision;
                report.RecordCollision(collision, context.Token!.StartIndex);
                break;
            }
        }
        sources.Add(added);
    }

    /// <summary>
    /// The object name, as written, that <paramref name="source"/> exposes
    /// because it carries no alias; null for an aliased source or a CTE
    /// reference. An alias spelled as the object's own last part reads as no
    /// alias.
    /// </summary>
    private static string? ExposedTableName(Collation collation, FromSource source)
    {
        if (source is { BackingTable: null, BackingView: null, BackingCatalogView: null } || source.WrittenObjectName is not { } written)
            return null;
        var lastDot = written.LastIndexOf('.');
        return collation.Equals(written[(lastDot + 1)..], source.Qualifier) ? written : null;
    }

    private static void ParseExplicitJoinChain(
        ParserContext context,
        QueryScope scope,
        List<FromSource> sources,
        List<JoinSpec> joins,
        List<Reference> siblingCandidates)
    {
        // A chain's ON predicates see its own sources alone — not an earlier
        // comma-separated item's, nor an enclosing chain's when this one is a
        // group (probed 2026-09-24 against SQL Server 2025: each is Msg 4104).
        var scopeStart = sources.Count;

        // A parenthesized join group as the leftmost item — `(A JOIN B ON …)
        // [LEFT] JOIN C …` — is a pure grammar grouping: a left-deep spine
        // already groups its left operand, so the group's interior sources /
        // joins splice directly into this chain with no group marker.
        if (NextSourceIsJoinGroup(context))
            ParseJoinGroup(context, scope, sources, joins, siblingCandidates);
        else if (NextSourceIsVectorSearch(context))
            AddVectorSearch(context, sources, joins, scope.OuterTypeResolver);
        else
            AddSource(context, sources, ParseSourceCollectingColumnReads(context, scope, sources, siblingCandidates));

        ParseJoinClauses(context, scope, sources, joins, siblingCandidates, scopeStart, nested: false);
    }

    /// <summary>
    /// Parses the JOIN clauses after a chain's leftmost source.
    /// ParseSingleFromSource ends with the cursor at the lookahead-after-source
    /// token (e.g. WHERE, ORDER, JOIN, INNER, LEFT, CROSS, etc.), so this loops
    /// while it sees a JOIN-introducing keyword, and stops at anything else — an
    /// <c>ON</c> included, which is how a nested chain hands the <c>ON</c> it
    /// doesn't own back to the join that does. <paramref name="scopeStart"/> is
    /// the chain's first source, the earliest an ON predicate may see;
    /// <paramref name="nested"/> marks an unparenthesized inner chain, where an
    /// ON after a cross join or APPLY is the enclosing join's.
    /// </summary>
    private static void ParseJoinClauses(
        ParserContext context,
        QueryScope scope,
        List<FromSource> sources,
        List<JoinSpec> joins,
        List<Reference> siblingCandidates,
        int scopeStart,
        bool nested)
    {
        while (TryParseJoinKeyword(context, out var kind))
        {
            if (kind is JoinKind.CrossApply or JoinKind.OuterApply)
            {
                if (NextSourceIsVectorSearch(context))
                {
                    AddAppliedVectorSearch(context, sources, joins, kind, scope);
                }
                else
                {
                    AddSource(context, sources, ParseLateralFromSource(context, scope, sources));
                    joins.Add(new JoinSpec(kind, onPredicate: null));
                }
                if (context.Token is ReservedKeyword { Keyword: Keyword.On } onToken)
                {
                    if (nested)
                        return;
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(onToken);
                }
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
            // A VECTOR_SEARCH is a two-member group of its own: the ON joins
            // the spine against the distance rowset and the table together.
            if (NextSourceIsVectorSearch(context))
            {
                var searchJoinIndex = joins.Count;
                AddVectorSearch(context, sources, joins, scope.OuterTypeResolver);
                BooleanExpression? searchOn = null;
                if (kind == JoinKind.Cross)
                {
                    if (context.Token is ReservedKeyword { Keyword: Keyword.On } searchOnToken)
                    {
                        if (nested)
                        {
                            joins.Insert(searchJoinIndex, new JoinSpec(kind, null) { GroupCount = 2 });
                            return;
                        }
                        throw SimulatedSqlException.SyntaxErrorNearKeyword(searchOnToken);
                    }
                }
                else
                {
                    if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    searchOn = ParseJoinOn(context, scope, sources, scopeStart);
                }
                joins.Insert(searchJoinIndex, new JoinSpec(kind, searchOn) { GroupCount = 2, ScopeStart = scopeStart, ScopeEnd = sources.Count });
                continue;
            }

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
                ParseJoinGroup(context, scope, sources, joins, siblingCandidates);
                var groupCount = sources.Count - groupStart;
                BooleanExpression? groupOn = null;
                if (kind == JoinKind.Cross)
                {
                    if (context.Token is ReservedKeyword { Keyword: Keyword.On })
                    {
                        if (nested)
                        {
                            joins.Insert(groupJoinIndex, new JoinSpec(kind, null) { GroupCount = groupCount });
                            return;
                        }
                        throw SimulatedSqlException.SyntaxErrorNearKeyword((ReservedKeyword)context.Token);
                    }
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
                    groupOn = ParseOnPredicateWithScope(context, sources, scopeStart, scope.OuterTypeResolver);
                }
                joins.Insert(groupJoinIndex, new JoinSpec(kind, groupOn) { GroupCount = groupCount, ScopeStart = scopeStart, ScopeEnd = sources.Count });
                continue;
            }

            // Joined-source derived tables can also correlate, but the JoinDriver
            // path for non-leftmost LateralPlan sources doesn't apply ON
            // predicates or LEFT-fill. Keep the chained outer-type-resolver in
            // play so a correlated derived table here is at least diagnosed
            // (NotSupportedException at execute time) rather than silently
            // resolving against a wrong scope.
            var rightStart = sources.Count;
            var joinIndex = joins.Count;
            AddSource(context, sources, ParseSourceCollectingColumnReads(context, scope, sources, siblingCandidates));
            BooleanExpression? on = null;
            if (kind == JoinKind.Cross)
            {
                if (context.Token is ReservedKeyword { Keyword: Keyword.On })
                {
                    if (nested)
                    {
                        joins.Add(new JoinSpec(kind, null));
                        return;
                    }
                    throw SimulatedSqlException.SyntaxErrorNearKeyword((ReservedKeyword)context.Token);
                }
            }
            else
            {
                if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
                {
                    // Another join before this one's ON: `A LEFT JOIN B JOIN C
                    // ON c1 ON c2` nests as `A LEFT JOIN (B JOIN C ON c1) ON c2`
                    // does, the inner chain taking the first ON and this join
                    // the next (probed 2026-09-24 against SQL Server 2025).
                    ParseJoinClauses(context, scope, sources, joins, siblingCandidates, rightStart, nested: true);
                    if (joins.Count == joinIndex || context.Token is not ReservedKeyword { Keyword: Keyword.On })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    joins.Insert(joinIndex, new JoinSpec(kind, ParseJoinOn(context, scope, sources, scopeStart)) { GroupCount = sources.Count - rightStart, ScopeStart = scopeStart, ScopeEnd = sources.Count });
                    continue;
                }
                on = ParseJoinOn(context, scope, sources, scopeStart);
            }
            joins.Add(new JoinSpec(kind, on) { ScopeStart = scopeStart, ScopeEnd = sources.Count });
        }
    }

    /// <summary>
    /// Parses a join's <c>ON</c> predicate, the cursor on the <c>ON</c>. An ON
    /// predicate rejects NEXT VALUE FOR (Msg 11720), like the other clauses
    /// real names in that message.
    /// </summary>
    private static BooleanExpression ParseJoinOn(ParserContext context, QueryScope scope, List<FromSource> sources, int scopeStart)
    {
        context.MoveNextRequired();
        using var rejection = context.EnterNextValueForScope(NextValueForScope.Clause);
        return ParseOnPredicateWithScope(context, sources, scopeStart, scope.OuterTypeResolver);
    }

    /// <summary>
    /// Parses a FROM source's argument list with the source's scope as the
    /// outer scope of any subquery inside it, so a subquery argument under
    /// <c>APPLY</c> correlates to the left side as a bare column argument
    /// does (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static T InArgumentScope<T>(ParserContext context, QueryScope scope, Func<T> parse)
    {
        if (scope.OuterTypeResolver is null)
            return parse();
        using var outer = ParserScope.Enter(ref context.OuterTypeResolver, scope.OuterTypeResolver);
        return parse();
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
    private static BooleanExpression ParseOnPredicateWithScope(ParserContext context, List<FromSource> sources, int scopeStart, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var scope = sources.GetRange(scopeStart, sources.Count - scopeStart).ToArray();
        // An aggregate the ON would own — written there, or moved there from a
        // subquery reading only the join's columns — is Msg 1015; one reading
        // only an enclosing query's columns belongs to that query.
        var onAggregates = new List<AggregateExpression>();
        BooleanExpression predicate;
        using (ParserScope.Enter(ref context.OuterTypeResolver, name => ResolveColumnTypeAcrossSources(scope, name, outerTypeResolver)))
        // A MATCH in an ON binds too, against the ON's own sources, every one
        // of them joined (Msg 13920).
        using (ParserScope.Enter(ref context.MatchScope, new Expressions.MatchScope { Sources = scope, AllJoined = true }))
        using (ParserScope.Enter(ref context.AggregateCollector, onAggregates))
        {
            predicate = BooleanExpression.SimplifyForFilter(BooleanExpression.Parse(context), context);
        }
        RehomeAggregatesOverOuterScope(context.Batch, [.. sources], onAggregates, outerTypeResolver);
        RefuseClauseAggregates(context.Batch, onAggregates, SimulatedSqlException.AggregateInOnClause());
        return predicate;
    }

    /// <summary>
    /// Peeks whether the FROM source about to be parsed is a parenthesized
    /// join group — an opening <c>(</c> whose first interior token is not
    /// <c>SELECT</c> (a derived table), <c>VALUES</c> (a table-value
    /// constructor) or <c>WITH</c> (a CTE prefix, which no query in a
    /// parenthesized position may carry — routing it to the derived-table
    /// branch is what gets it real's Msg 156 instead of a join group's
    /// Msg 102), nor a parenthesized query (<see cref="LeadsParenthesizedQuery"/>,
    /// a derived table over a set operation of parenthesized branches).
    /// Entered with the cursor on the token preceding the source
    /// (<c>FROM</c> / a JOIN keyword / a comma / the group's own <c>(</c> when
    /// this is an interior leftmost), matching the one-token lookahead
    /// <see cref="ParseSingleFromSource"/> consumes; the checkpoint is restored
    /// so the dispatch is non-destructive.
    /// </summary>
    private static bool NextSourceIsJoinGroup(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var opensGroup = context.GetNextOptional() is Operator { Character: '(' } && context.GetNextOptional() switch
        {
            null or ReservedKeyword { Keyword: Keyword.Select or Keyword.Values or Keyword.With } => false,
            Operator { Character: '(' } => !LeadsParenthesizedQuery(context, closeCounts: true),
            _ => true,
        };
        context.RestoreCheckpoint(checkpoint);
        return opensGroup;
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
        QueryScope scope,
        List<FromSource> sources,
        List<JoinSpec> joins,
        List<Reference> siblingCandidates)
    {
        var joinsBefore = joins.Count;
        context.MoveNextRequired();
        ParseExplicitJoinChain(context, scope, sources, joins, siblingCandidates);
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
        QueryScope scope)
    {
        using var rejection = ParserScope.Save(ref context.NextValueForRejection);
        if (!context.AllowNextValueForInFromClause)
            context.RaiseNextValueForFloor(NextValueForScope.Nested);
        return Selection.Parse(context, scope);
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
        QueryScope scope,
        List<FromSource> sourcesSoFar,
        List<Reference> siblingCandidates)
    {
        var collectedBefore = siblingCandidates.Count;
        using var sink = ParserScope.Enter(ref context.FromSourceColumnSink, siblingCandidates);
        try
        {
            return ParseSingleFromSource(context, scope);
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
                sourcesSoFar, scope.OuterTypeResolver);
            throw;
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
        QueryScope scope,
        List<FromSource> leftSources)
    {
        // Peek next token. A parenthesized derived table `(SELECT ...)`
        // stays on the dedicated path so the chained outer-type resolver
        // can be wired into the inner Selection's parse. A leading Name
        // that resolves to a TVF parses with the left sources in scope;
        // any other name is a plain table or view.
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextRequired();

        // OPENQUERY and OPENXML are reserved keywords (not Names), so neither
        // can ride the name-string dispatch below. Neither correlates to the
        // left APPLY sources — OPENQUERY's arguments are a server identifier
        // and a constant pass-through string, OPENXML's a session document
        // handle and its patterns — so route them straight back through
        // ParseSingleFromSource.
        if (next is ReservedKeyword { Keyword: Keyword.OpenQuery or Keyword.OpenXml or Keyword.OpenRowSet or Keyword.OpenDataSource })
        {
            context.RestoreCheckpoint(checkpoint);
            return ParseSingleFromSource(context, scope);
        }

        // `CROSS APPLY @x.nodes('…')` shreds a variable, which needs no left scope.
        if (next is AtPrefixedString && IsXmlNodesCallAhead(context))
            return ParseXmlNodesSource(context, []);

        if (next is Name nextName)
        {
            // The right-side source's column references can correlate to the
            // left sources of the APPLY — wire the chained resolver up front
            // so OPENJSON / STRING_SPLIT / user TVF parse-time GetSqlType
            // calls reach them.
            var leftSnapshotForName = leftSources.ToArray();
            var chainedResolverForName = TypeResolverOver(leftSnapshotForName, scope.OuterTypeResolver);

            // Built-in rowset functions (OPENJSON, STRING_SPLIT,
            // GENERATE_SERIES) share the same APPLY-friendly shape as user-
            // defined inline TVFs — route them back through
            // ParseSingleFromSource. Case-insensitive match to mirror real
            // SQL Server's grammar.
            if (string.Equals(nextName.Value, "OPENJSON", StringComparison.OrdinalIgnoreCase)
                || string.Equals(nextName.Value, "STRING_SPLIT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(nextName.Value, "GENERATE_SERIES", StringComparison.OrdinalIgnoreCase)
                || IsChangeTableName(nextName.Value)
                || IsRegexpRowsetName(nextName.Value, context))
            {
                context.RestoreCheckpoint(checkpoint);
                return AcrossApplyBoundary(context, leftSnapshotForName, () => ParseSingleFromSource(context, scope.WithOuter(chainedResolverForName)));
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

            var resolvedIsTvf = IsSysRowsetFunction(resolvedName)
                || context.Batch.TryResolveTableValuedFunction(resolvedName, out _);
            // A '(' after the name marks a function-call shape (TVF invocation).
            // ParseObjectName leaves the cursor on the leaf; peek one past it.
            var isFunctionCallShape = context.MoveNext() && context.Token is Operator { Character: '(' };
            context.RestoreCheckpoint(checkpoint);
            if (resolvedIsTvf)
            {
                // An argument reading the left side's masked column masks the function's columns.
                var tvfOuterMask = context.OuterMaskResolver;
                using var outerMask = ParserScope.Save(ref context.OuterMaskResolver);
                if (context.Batch.Connection.Simulation.DeclaresDataMasks)
                    context.OuterMaskResolver = name => ScopedColumnMask(leftSnapshotForName, name, tvfOuterMask);
                return AcrossApplyBoundary(context, leftSnapshotForName, () => ParseSingleFromSource(context, scope.WithOuter(chainedResolverForName)));
            }
            // A function-call shape that didn't resolve to a known TVF is a
            // deferred name-resolution error (Msg 208), not a syntax error:
            // real SQL Server binds the TVF name lazily, so an un-taken IF
            // branch naming an unknown function (SSMS's EngineEdition-gated
            // `CROSS APPLY sys.dm_os_volume_stats(...)` VolumeFreeSpace probe)
            // compiles and is discarded.
            // A scalar function named as a rowset is an object the reference
            // can't use, state 224 (probed 2026-10-04 against SQL Server 2025).
            if (isFunctionCallShape && context.Batch.TryResolveFunction(resolvedName, out var applied) && applied is ScalarFunction)
                throw SimulatedSqlException.InvalidObjectName(resolvedName, state: 224);
            if (isFunctionCallShape && context.Batch.IsSkipping)
            {
                InlinedScalarCalls.NoteMissingObject(context.Batch, resolvedName, context.Token?.LineNumber ?? 0);
                // The compile pass carries on past the missing function so a
                // syntax error later in the statement outranks its Msg 208,
                // as it does for a missing table (probed 2026-09-29).
                context.RestoreCheckpoint(afterNameCheckpoint);
                _ = BatchContext.ParseObjectName(context);
                context.MoveNextRequired();
                SkipBalancedParens(context);
                context.Batch.CurrentStatement.BindsDeferredSource = true;
                var placeholderAlias = ConsumeOptionalAlias(context);
                return FromSource.DeferredPlaceholder(placeholderAlias ?? resolvedName.Leaf);
            }
            if (isFunctionCallShape)
                throw SimulatedSqlException.InvalidObjectName(resolvedName);
            // A table or view after APPLY reads as a cross / outer join with
            // nothing to correlate (probed 2026-10-02 against SQL Server 2025:
            // `CROSS APPLY t`, `OUTER APPLY dbo.t AS x`; EF Core emits the
            // latter for a nested SelectMany).
            return ParseSingleFromSource(context, scope);
        }
        if (next is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var afterApplyParen = context.GetNextRequired();

        var leftSnapshot = leftSources.ToArray();
        var chainedResolver = TypeResolverOver(leftSnapshot, scope.OuterTypeResolver);

        // CROSS / OUTER APPLY (VALUES (…), (…)) alias(cols): the table value
        // constructor's rows can reference the left APPLY sources — the SSMS
        // dm_os_host_info server-properties shape. The chained resolver wires
        // that correlation in at parse and (via ForValuesConstructor) runtime.
        if (afterApplyParen is ReservedKeyword { Keyword: Keyword.Values })
        {
            // A cell reading the left side's masked column masks the column it feeds.
            var valuesOuterMask = context.OuterMaskResolver;
            using var outerMask = ParserScope.Save(ref context.OuterMaskResolver);
            if (context.Batch.Connection.Simulation.DeclaresDataMasks)
                context.OuterMaskResolver = name => ScopedColumnMask(leftSnapshot, name, valuesOuterMask);
            return AcrossApplyBoundary(context, leftSnapshot, () => ParseValuesDerivedTable(context, chainedResolver));
        }

        if (afterApplyParen is not (ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: '(' }))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // The body projects the left side's masked columns as its own.
        var savedOuterMask = context.OuterMaskResolver;
        Selection lateralPlan;
        using (ParserScope.Save(ref context.OuterMaskResolver))
        {
            if (context.Batch.Connection.Simulation.DeclaresDataMasks)
                context.OuterMaskResolver = name => ScopedColumnMask(leftSnapshot, name, savedOuterMask);
            lateralPlan = AcrossApplyBoundary(context, leftSnapshot, () => ParseNestedQueryRejectingNextValueFor(context, QueryScope.Nested(QueryPosition.Derived, chainedResolver)));
        }

        var schema = lateralPlan.Schema;
        var columnNames = lateralPlan.ColumnNames;
        var lateralColumns = new HeapColumn[schema.Length];
        for (var ci = 0; ci < lateralColumns.Length; ci++)
            lateralColumns[ci] = new HeapColumn(string.Empty, schema[ci], maxLength: null, nullable: lateralPlan.ColumnNullability?[ci] ?? true) { AliasType = lateralPlan.ColumnAliasTypes?[ci], IdentitySource = lateralPlan.ColumnIdentitySources?[ci], DerivedMask = lateralPlan.ColumnMasks?[ci] };

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
            lateralIsQueryBody: true,
            derivedTable: alias is null ? null : new DerivedTableBinding(lateralPlan, alias, columnNames, context.CurrentDatabase, correlated: true));
    }

    /// <summary>
    /// Parses an <c>APPLY</c>'s right side through <paramref name="parse"/>
    /// with an <see cref="ApplyAggregateBoundary"/> as its enclosing
    /// collector: an aggregate moving out of the right side stops there when
    /// it reads <paramref name="leftSources"/> (Msg 4101) and otherwise goes on
    /// to the query holding the <c>APPLY</c>. One a function argument registers
    /// directly makes the same move once the parse is done.
    /// </summary>
    private static T AcrossApplyBoundary<T>(ParserContext context, FromSource[] leftSources, Func<T> parse)
    {
        var boundary = new ApplyAggregateBoundary(leftSources, context.AggregateCollector);
        T parsed;
        using (ParserScope.Enter(ref context.AggregateCollector, boundary))
        {
            parsed = parse();
        }
        foreach (var aggregate in boundary)
        {
            if (!MoveToEnclosingQuery(context.Batch, aggregate, boundary))
                throw new NotSupportedException("An aggregate in an APPLY's function argument, with no enclosing query collecting aggregates, isn't modeled.");
        }
        return parsed;
    }

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
    private static Schemas.Synonym? RecordSecurableRead(ParserContext context, Schemas.SchemaObject obj, MultiPartName name, Selection? moduleBody = null)
    {
        var synonym = context.Batch.TryResolveSynonym(name, out var resolved) ? resolved : null;
        context.PartitionedWriteReads?.Add(obj);
        if (context.SecurableSink is { } sink && !name.Leaf.StartsWith('#'))
        {
            var securable = (Schemas.SchemaObject?)synonym ?? obj;
            var database = context.Batch.DatabaseFor(securable);
            // An unqualified name reports the schema it resolved into — the
            // principal's default one, not necessarily dbo.
            var schemaName = name.Count >= 2 && !name.SchemaOmitted ? name.ImmediateQualifier! : SchemaNameOf(database, securable.SchemaId);
            sink.Add(new ReferencedSecurable(database, securable, schemaName, module: moduleBody is null ? null : obj as Schemas.View, moduleBody: moduleBody));
        }
        return synonym;
    }

    /// <summary>The name of <paramref name="database"/>'s schema <paramref name="schemaId"/>.</summary>
    private static string SchemaNameOf(Database database, int schemaId)
    {
        if (schemaId != Database.DboSchemaId)
        {
            foreach (var (_, schema) in database.Schemas)
            {
                if (schema.SchemaId == schemaId)
                    return schema.Name;
            }
        }
        return Database.DefaultSchemaName;
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

    /// <summary>
    /// Parses one FROM source (see <see cref="ParseSingleFromSourceCore"/>)
    /// and applies any trailing <c>PIVOT</c> / <c>UNPIVOT</c> table operator.
    /// The postfix wrapper lives here so both the leftmost source and every
    /// join-right source pick up PIVOT / UNPIVOT without changing their call
    /// sites; the cursor-after-source contract is preserved either way (a
    /// PIVOT / UNPIVOT clause consumes through its own alias and stops at the
    /// next lookahead token).
    /// </summary>
    private static FromSource ParseSingleFromSource(ParserContext context, QueryScope scope)
    {
        var source = ParseSingleFromSourceCore(context, scope);
        // FOR SYSTEM_TIME belongs straight after a table's or view's name, so
        // one past an alias or hints — or after any other source — is a syntax
        // error at FOR (probed 2026-10-04 against SQL Server 2025).
        if (context.Token is ReservedKeyword { Keyword: Keyword.For } forKeyword)
        {
            var atFor = context.SaveCheckpoint();
            var followsSystemTime = context.GetNextOptional() is UnquotedString { ContextualKeyword: ContextualKeyword.System_Time };
            context.RestoreCheckpoint(atFor);
            if (followsSystemTime)
                throw SimulatedSqlException.SyntaxErrorNearText(forKeyword.Source.ToString());
        }
        return ApplyOptionalPivotUnpivot(context, source, scope.OuterTypeResolver);
    }

    /// <summary>
    /// Parses one FROM source: a table name (with optional alias) or a
    /// derived-table <c>(SELECT ...)</c> (with optional alias). On entry
    /// the cursor is on the FROM or JOIN keyword (caller advances past it
    /// internally via <see cref="ParserContext.GetNextRequired"/>); on
    /// return, the cursor is at the first un-consumed token after the
    /// source — typically WHERE / ORDER / a JOIN keyword / ON / etc.
    /// </summary>
    private static FromSource ParseSingleFromSourceCore(ParserContext context, QueryScope scope)
    {
        var token = context.GetNextRequired();
        // A nested DML source feeding a linked server's table is refused
        // before its own grammar is judged; a parenthesized source reaches
        // here through the join-group parse.
        if (token is ReservedKeyword { Keyword: Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge }
            && context.Batch.CurrentStatement.RemoteWrite is not null)
        {
            throw SimulatedSqlException.RemoteDmlTargetWithOutput();
        }
        // Every source but a derived table or VALUES — a table, view, CTE,
        // table variable, rowset function or catalog view — opens the
        // statement's transaction; a derived table opens it only through a
        // source of its own.
        if (token is not Operator { Character: '(' })
            context.Batch.CurrentStatement.MarkOpensTransaction();
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
                    return BuiltInRowsetSource(context, InArgumentScope(context, scope, () => ParseOpenJson(context, scope.OuterTypeResolver)));

                if (string.Equals(tableName.Value, "STRING_SPLIT", StringComparison.OrdinalIgnoreCase))
                    return BuiltInRowsetSource(context, InArgumentScope(context, scope, () => ParseStringSplit(context, scope.OuterTypeResolver)));

                // GENERATE_SERIES: single-column (`value`) plan, SQL Server 2022+.
                if (string.Equals(tableName.Value, "GENERATE_SERIES", StringComparison.OrdinalIgnoreCase))
                    return BuiltInRowsetSource(context, InArgumentScope(context, scope, () => ParseGenerateSeries(context, scope.OuterTypeResolver)));

                if (IsChangeTableName(tableName.Value))
                    return InArgumentScope(context, scope, () => ParseChangeTableSource(context, scope));

                // The two REGEXP rowset members ship only at compatibility
                // level 170; below it the name falls through to the ordinary
                // object-name path, which raises the Msg 208 real raises.
                if (IsRegexpRowsetName(tableName.Value, context))
                {
                    return BuiltInRowsetSource(context, InArgumentScope(context, scope, () => string.Equals(tableName.Value, "REGEXP_MATCHES", StringComparison.OrdinalIgnoreCase)
                        ? ParseRegexpMatches(context, scope.OuterTypeResolver)
                        : ParseRegexpSplitToTable(context, scope.OuterTypeResolver)));
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
                var beforeObjectName = context.SaveCheckpoint();
                var objectNameLine = context.Token?.LineNumber ?? 0;
                var objectName = BatchContext.ParseObjectName(context);

                // `FROM t.x.nodes('…') n(c)` in a subquery shreds a column of
                // an enclosing query, as an APPLY's right side shreds one of
                // its left side (probed 2026-10-02 against SQL Server 2025).
                if (objectName.Count > 1 && objectName.Leaf.Equals("nodes", StringComparison.Ordinal))
                {
                    var afterNodesLeaf = context.SaveCheckpoint();
                    var followedByParen = context.MoveNext() && context.Token is Operator { Character: '(' };
                    context.RestoreCheckpoint(afterNodesLeaf);
                    if (followedByParen)
                    {
                        context.RestoreCheckpoint(beforeObjectName);
                        return ParseXmlNodesSource(context, []);
                    }
                }

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

                // The two permission functions take the same bare or `sys.`
                // forms.
                if (objectName.Count == 1 || (objectName.Count == 2 && BuiltInToken.Equals(objectName.ImmediateQualifier, "sys")))
                {
                    if (BuiltInToken.Equals(objectName.Leaf, "fn_builtin_permissions"))
                        return BuiltInRowsetSource(context, ParseBuiltinPermissions(context, objectName.Leaf));
                    if (BuiltInToken.Equals(objectName.Leaf, "fn_my_permissions"))
                        return BuiltInRowsetSource(context, ParseMyPermissions(context, objectName.Leaf));
                }

                // The dependency DMVs, the describe DMV, dm_exec_sql_text,
                // dm_exec_input_buffer, dm_exec_cursors and
                // dm_io_virtual_file_stats are system TVFs, `sys.`-qualified like
                // fn_virtualfilestats and dispatched on the same terms.
                if (objectName.Count == 2 && BuiltInToken.Equals(objectName.ImmediateQualifier, "sys"))
                {
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_sql_referencing_entities"))
                        return BuiltInRowsetSource(context, ParseSqlReferencingEntities(context, objectName.ToString()));
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_sql_referenced_entities"))
                        return BuiltInRowsetSource(context, ParseSqlReferencedEntities(context, objectName.ToString()));
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_exec_describe_first_result_set"))
                        return BuiltInRowsetSource(context, ParseDescribeFirstResultSet(context, objectName.ToString()));
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_exec_sql_text"))
                        return BuiltInRowsetSource(context, ParseSqlText(context, objectName.ToString()));
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_exec_input_buffer"))
                        return BuiltInRowsetSource(context, ParseInputBuffer(context, objectName.ToString()));
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_exec_cursors"))
                        return BuiltInRowsetSource(context, ParseExecCursors(context, objectName.ToString()));
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_fts_parser"))
                        return BuiltInRowsetSource(context, ParseFtsParser(context, objectName.ToString()));
                    if (BuiltInToken.Equals(objectName.Leaf, "dm_io_virtual_file_stats"))
                        return BuiltInRowsetSource(context, ParseVirtualFileStatsDmv(context, objectName.ToString()));
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
                    context.Batch.BeginImplicitTransaction();

                    // The alias an UPDATE or DELETE named as its target makes
                    // this source the linked server's stand-in for the write.
                    if (context.Batch.CurrentStatement.RemoteWriteAlias is { } targetAlias)
                    {
                        var aliasCheckpoint = context.SaveCheckpoint();
                        var writtenAlias = ConsumeOptionalAlias(context);
                        context.RestoreCheckpoint(aliasCheckpoint);
                        if (writtenAlias is not null && context.CurrentDatabase.Collation.Equals(writtenAlias, targetAlias)
                            && RemoteWrite.ForTarget(context.Batch, objectName, context.Batch.CurrentStatement.RemoteWriteAliasKind) is { } remoteTarget)
                        {
                            return RemoteTargetSource(context, remoteTarget, ConsumeOptionalAlias(context), objectName.ToString());
                        }
                    }

                    if (!context.Batch.TryResolveLinkedServerTable(objectName, out var linkedServer, out var remoteName, out var remoteColumns, out var remoteDbName, out var remoteSchemaName))
                    {
                        if (TryRemoteCatalogViewSource(context, objectName) is { } catalogSource)
                        {
                            context.RemoteSourcesParsed++;
                            return catalogSource;
                        }
                        // Real checks the server's metadata compiling the
                        // batch, so a branch the batch never takes raises it.
                        throw SimulatedSqlException.RemoteTableNotFound(RemoteWrite.ResolveServer(context.Batch, objectName[0]), RemoteWrite.QuotedName(objectName));
                    }
                    _ = RemoteWrite.ResolveServer(context.Batch, objectName[0]);
                    if (!context.Batch.IsSkipping && context.Connection.CurrentTransaction is { IsDistributed: true })
                        RemoteWrite.RequireNoTransaction(context.Batch, linkedServer);
                    var linkedColumnNames = new string[remoteColumns.Length];
                    for (var ci = 0; ci < linkedColumnNames.Length; ci++)
                        linkedColumnNames[ci] = remoteColumns[ci].Name;
                    var linkedAlias = ConsumeOptionalAlias(context);
                    context.RemoteSourcesParsed++;
                    _ = ParseOptionalTableHints(context);
                    return new FromSource(
                        qualifier: linkedAlias ?? remoteName,
                        columnNames: linkedColumnNames,
                        columns: remoteColumns,
                        storedSchema: remoteColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: Selection.ForLinkedServer(linkedServer, remoteDbName, remoteSchemaName, remoteName, remoteColumns));
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
                            rows: SelfReferenceRows(cteBinding),
                            writtenObjectName: cteBinding.Name);
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

                    // A name the CTE projects twice is refused where it is
                    // read, as a derived table's is — an unused CTE reports
                    // Msg 422 instead (probed 2026-09-24).
                    _ = RejectRepeatedColumnName(cteBinding.ColumnNames, cteBinding.Name);

                    var cteColumns = new HeapColumn[cteBinding.Plan.Schema.Length];
                    for (var ci = 0; ci < cteColumns.Length; ci++)
                    {
                        cteColumns[ci] = new HeapColumn(string.Empty, cteBinding.Plan.Schema[ci], maxLength: null, nullable: cteBinding.Plan.ColumnNullability?[ci] ?? true, spelledNumeric: cteBinding.Plan.ColumnReportsNumeric is { } cteNumeric && cteNumeric[ci])
                        {
                            IsUntypedNull = cteBinding.Plan.ColumnIsUntypedNull is { } cteNulls && cteNulls[ci],
                            AliasType = cteBinding.Plan.ColumnAliasTypes?[ci],
                            IdentitySource = cteBinding.Plan.ColumnIdentitySources?[ci],
                            DerivedMask = cteBinding.Plan.ColumnMasks?[ci],
                        };
                    }

                    var cteAlias = ConsumeOptionalAlias(context);
                    // A CTE reference takes no hints, so a WITH after it is the
                    // start of another CTE the statement ran into (Msg 336).
                    if (context.Token is ReservedKeyword { Keyword: Keyword.With })
                    {
                        var afterWith = context.SaveCheckpoint();
                        if (context.GetNextOptional() is Name nextCte)
                            throw SimulatedSqlException.CteAfterUnterminatedStatement(nextCte.Value);
                        context.RestoreCheckpoint(afterWith);
                    }
                    // A parenthesized list after it is the legacy hint form
                    // once an alias is written, and an argument list (Msg 215)
                    // without one, even for a hint name (probed 2026-10-01
                    // against SQL Server 2025).
                    if (context.Token is Operator { Character: '(' })
                    {
                        if (cteAlias is null)
                            RefuseArgumentList(context, cteBinding.Name, reportsNames: false);
                        _ = ParseOptionalTableHints(context, commitOnLegacyParen: true);
                    }

                    return new FromSource(
                        qualifier: cteAlias ?? cteBinding.Name,
                        columnNames: cteBinding.ColumnNames,
                        columns: cteColumns,
                        storedSchema: cteColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: cteBinding.Plan,
                        lateralIsQueryBody: true,
                        // A CTE is an object with a name of its own, so real's
                        // diagnostics report the CTE rather than the alias the
                        // reference wrote — `FROM c AS q` names `c` (probed
                        // 2026-08-08, GROUP BY containment and XQuery alike).
                        writtenObjectName: cteBinding.Name,
                        cte: cteBinding);
                }

                // Past a CTE the name is an object — a table, view, table
                // variable, catalog view or function — which opens an implicit
                // transaction.
                context.Batch.BeginImplicitTransaction();

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
                        backingCatalogDatabase: catalogTargetDb,
                        writtenObjectName: objectName.ToString(),
                        unaliasedName: catalogAlias is null ? FromSource.Resolved(objectName, context.Batch.CurrentDatabase) : null);
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
                    // FOR SYSTEM_TIME after a view's name applies to the tables
                    // its body reads (probed 2026-10-04 against SQL Server 2025).
                    var viewSystemTime = ParseOptionalForSystemTimeClause(context);
                    var viewColumns = context.Batch.Connection.Simulation.BindViewColumns(context.Batch, resolvedView, objectName, out var viewBody, viewSystemTime);
                    var viewColumnNames = new string[viewColumns.Length];
                    for (var ci = 0; ci < viewColumnNames.Length; ci++)
                        viewColumnNames[ci] = viewColumns[ci].Name;
                    var viewAlias = viewSystemTime is null ? ConsumeOptionalAlias(context) : ConsumeOptionalAliasAtCurrent(context);
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
                    ValidateViewIndexHints(context, resolvedView, viewHints, objectName.ToString(), objectNameLine);
                    var viewSynonym = RecordSecurableRead(context, resolvedView, objectName, viewBody);
                    return new FromSource(
                        qualifier: viewAlias ?? resolvedView.Name,
                        columnNames: viewColumnNames,
                        columns: viewColumns,
                        storedSchema: viewColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: Selection.ForView(resolvedView, viewColumns, systemTime: viewSystemTime),
                        backingView: resolvedView,
                        viaSynonym: viewSynonym,
                        autoElementName: viewAlias ?? objectName.ToString(),
                        writtenObjectName: objectName.ToString(),
                        unaliasedName: viewAlias is null ? FromSource.Resolved(objectName, context.Batch.CurrentDatabase) : null);
                }

                // TVF call from FROM clause: `FROM schema.fn(args) [alias]`.
                // Detected when the resolved function is an inline or
                // multi-statement TVF AND `(` follows the name (cursor is on
                // the name leaf post-ParseObjectName; peek the next token via
                // a checkpoint). A ScalarFunction here falls through to the
                // table-lookup branch and surfaces Msg 208 (probe-confirmed:
                // real SQL Server treats `FROM dbo.scalar_fn(...)` as a
                // missing-object error, not a kind-mismatch).
                if (context.Batch.TryResolveTableValuedFunction(objectName, out var function))
                {
                    var checkpoint = context.SaveCheckpoint();
                    context.MoveNextOptional();
                    if (context.Token is Operator { Character: '(' })
                    {
                        context.MoveNextRequired();
                        var tvfArgs = InArgumentScope(context, scope, () => Expressions.UserFunctionCall.ParseFunctionArguments(function, context));
                        // ParseFunctionArguments leaves the cursor on the closing `)`.
                        var tvfAlias = ConsumeOptionalAlias(context);
                        // A user function's columns take no alias list (probed
                        // 2026-10-04 against SQL Server 2025).
                        if (tvfAlias is not null && context.Token is Operator { Character: '(' })
                            throw SimulatedSqlException.TableValuedFunctionColumnAlias(function.Name);
                        var outputColumns = function switch
                        {
                            InlineTableValuedFunction inline => Simulation.InlineTvfColumnsWithCurrentMasks(context, inline),
                            ClrTableValuedFunction clr => clr.OutputColumns,
                            _ => ((MultiStatementTableValuedFunction)function).OutputColumns,
                        };
                        // An argument reading a masked column taints every column the
                        // function returns, as default() (probed 2026-09-29 against SQL
                        // Server 2025: an email() argument reads `xxxx` too).
                        if (context.Batch.Connection.Simulation.DeclaresDataMasks && context.OuterMaskResolver is { } argumentMasks)
                        {
                            DataMask? taint = null;
                            foreach (var argument in tvfArgs)
                            {
                                if (argument is not null && DataMask.Of(argument, argumentMasks, typeOf: null) is { } argumentMask)
                                    taint = DataMask.Merge(taint, new DataMask(MaskingFunction.Default, argumentMask.Sources));
                            }
                            if (taint is not null)
                                outputColumns = [.. outputColumns.Select(column => column.WithDerivedMask(DataMask.Merge(column.DerivedMask, taint)))];
                        }
                        _ = RecordSecurableRead(context, function, objectName);
                        _ = context.IndexedViewShapeCollector?.TableValuedFunction ??= $"{function.Schema.Name}.{function.Name}";
                        // An inline function's body expands into the query, and
                        // the scalar functions it calls inline with it.
                        if (function is InlineTableValuedFunction expanded)
                            InlinedScalarCalls.Note(context, expanded);
                        var lateralPlan = function switch
                        {
                            InlineTableValuedFunction inlineTvf => Selection.ForInlineTvf(inlineTvf, tvfArgs, objectName),
                            ClrTableValuedFunction clrTvf => Selection.ForClrTvf(clrTvf, tvfArgs),
                            _ => Selection.ForMultiStatementTvf((MultiStatementTableValuedFunction)function, tvfArgs),
                        };
                        return new FromSource(
                            qualifier: tvfAlias ?? function.Name,
                            columnNames: [.. outputColumns.Select(c => c.Name)],
                            columns: outputColumns,
                            storedSchema: outputColumns,
                            storageOrdinals: null,
                            lobStore: null,
                            rows: [],
                            lateralPlan: lateralPlan,
                            unaliasedName: tvfAlias is null ? FromSource.Resolved(objectName, context.Batch.CurrentDatabase) : null);
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
                        InlinedScalarCalls.NoteMissingObject(context.Batch, objectName, context.Token?.LineNumber ?? 0);
                        var probe = context.SaveCheckpoint();
                        context.MoveNextOptional();
                        if (context.Token is Operator { Character: '(' })
                            SkipBalancedParens(context);
                        else
                            context.RestoreCheckpoint(probe);
                        context.Batch.CurrentStatement.BindsDeferredSource = true;
                        // A clause leaves the cursor on the token after it, where an
                        // absent one leaves it on the name (see the resolved path below).
                        var beforeClause = context.Token?.StartIndex;
                        _ = ParseOptionalForSystemTime(context, heapTable: null);
                        var placeholderAlias = context.Token?.StartIndex == beforeClause
                            ? ConsumeOptionalAlias(context)
                            : ConsumeOptionalAliasAtCurrent(context);
                        ParseOptionalTableSample(context);
                        _ = ParseOptionalFromSourceHints(context, placeholderAlias is not null, objectName.ToString());
                        return FromSource.DeferredPlaceholder(placeholderAlias ?? objectName.Leaf);
                    }
                    // A scalar function named as a rowset is an object the
                    // reference can't use, state 224 (probed 2026-10-04
                    // against SQL Server 2025).
                    if (context.Batch.TryResolveFunction(objectName, out var notRowset) && notRowset is ScalarFunction)
                        throw SimulatedSqlException.InvalidObjectName(objectName, state: 224);
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
                var forPath = ParseOptionalForPath(context);
                var temporalRowSource = forPath ? null : ParseOptionalForSystemTime(context, heapTable);
                var clauseParsed = temporalRowSource is not null;
                // A FOR SYSTEM_TIME a view reference carries applies to every
                // system-versioned table its body reads, one of which may not
                // carry its own (Msg 13590; probed 2026-10-04 against SQL
                // Server 2025).
                if (context.Batch.InheritedSystemTime is { } inherited && heapTable is { SystemVersioning: { } inheritedHistory, PeriodColumns: { } inheritedPeriod })
                {
                    if (clauseParsed)
                        throw SimulatedSqlException.ForSystemTimeAppliedTwice(objectName.ToString());
                    inherited.Applied = true;
                    temporalRowSource = new TemporalRowSource(heapTable, inheritedHistory, inheritedPeriod, inherited.Kind,
                        inherited.Lower is { } inheritedLower ? new Value(inheritedLower) : null,
                        inherited.Upper is { } inheritedUpper ? new Value(inheritedUpper) : null);
                }

                // FOR SYSTEM_TIME leaves the cursor at the post-clause lookahead
                // token (its ALL / AS-OF-expr parse already advanced past the
                // clause), whereas the no-clause path leaves it on the table
                // name leaf. ConsumeOptionalAlias advances before checking, so
                // after a temporal clause the current token is already the alias
                // candidate — consume it in place; otherwise use the advancing
                // form. Without this the token after a trailing WHERE / alias is
                // stranded and a draining consumer re-parses it into a spurious
                // syntax error.
                var heapAlias = clauseParsed
                    ? ConsumeOptionalAliasAtCurrent(context)
                    : ConsumeOptionalAlias(context);
                ParseOptionalTableSample(context);
                var heapHints = ParseOptionalFromSourceHints(context, heapAlias is not null, objectName.ToString());
                // NOEXPAND names an indexed view, which a table is not
                // (probed 2026-10-04 against SQL Server 2025).
                if (heapHints.NoExpand)
                    throw SimulatedSqlException.NoExpandHintInvalid(objectName.ToString(), state: 2).PinLine(objectNameLine + context.Batch.LineOffset);
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
                        ? new UnlockedScanRows(heapTable)
                        : (PerExecutionRows)new LockCheckedScanRows(heapTable, heapPlan));

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
                    writtenObjectName: objectName.ToString(),
                    unaliasedName: heapAlias is null ? FromSource.Resolved(objectName, context.Batch.CurrentDatabase) : null)
                {
                    ForPath = forPath,
                    ForcedAccessPath = heapHints.ForceSeek || (heapHints.ForceScan && heapHints.IndexArguments is { Count: > 0 }) ? heapHints : null,
                };

            // Table-variable source: <c>FROM @t [alias]</c>. Routes through
            // BatchContext.TableVariables instead of the regular schema dict;
            // missing @t raises Msg 1087 (distinct from regular tables'
            // Msg 208) since the user's spelling tells us they meant a
            // table variable, not a missing table.
            case AtPrefixedString:
                // `@x.nodes('…')` shreds an xml variable (or parameter); a
                // variable needs no APPLY-left scope for its target.
                if (IsXmlNodesCallAhead(context))
                    return ParseXmlNodesSource(context, []);
                var tvName = BatchContext.ParseObjectName(context, acceptTableVariable: true);
                if (!context.Batch.TryResolveTable(tvName, out var tvTable))
                    throw SimulatedSqlException.MustDeclareTableVariable(tvName.Leaf);
                context.Batch.BeginImplicitTransaction();
                var tvColumnNames = new string[tvTable.Columns.Length];
                for (var ci = 0; ci < tvColumnNames.Length; ci++)
                    tvColumnNames[ci] = tvTable.Columns[ci].Name;
                var tvAlias = ConsumeOptionalAlias(context);
                RejectTableVariableHints(context);
                return new FromSource(
                    qualifier: tvAlias ?? tvName.Leaf,
                    columnNames: tvColumnNames,
                    columns: tvTable.Columns,
                    storedSchema: tvTable.StoredColumns,
                    storageOrdinals: tvTable.StorageOrdinals,
                    lobStore: tvTable.Heap,
                    rows: new UnlockedScanRows(tvTable),
                    backingTable: tvTable,
                    writtenObjectName: tvName.Leaf);

            case Operator { Character: '(' }:
                var afterOpenParen = context.GetNextRequired();

                // Table-value-constructor derived table: `(VALUES …) alias(cols)`.
                // Rides the same deferred lateral-plan seam as a derived-table
                // SELECT, so a VALUES source correlates to outer scope the same
                // way (needed for a comma-FROM VALUES referencing an outer CTE).
                if (afterOpenParen is ReservedKeyword { Keyword: Keyword.Values })
                    return ParseValuesDerivedTable(context, context.OuterTypeResolver ?? scope.OuterTypeResolver);

                if (afterOpenParen is not (ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: '(' }))
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
                // Selection) and the explicit <paramref name="scope"/> resolver
                // chain (set when this FROM source is itself nested inside
                // a subquery) are honored.
                var derivedSelection = ParseNestedQueryRejectingNextValueFor(context,
                    QueryScope.Nested(QueryPosition.Derived, context.OuterTypeResolver ?? scope.OuterTypeResolver));

                // Inner SELECT result rows are LOB-inline (projections never
                // emit LOB pointers because they have no destination Heap),
                // so build a HeapColumn[] schema from the SqlType[] so the
                // decoder still strips marker bytes for text/ntext/image
                // columns; lobStore is null because no chain to follow.
                var derivedColumns = new HeapColumn[derivedSelection.Schema.Length];
                for (var ci = 0; ci < derivedColumns.Length; ci++)
                {
                    derivedColumns[ci] = new HeapColumn(string.Empty, derivedSelection.Schema[ci], maxLength: null, nullable: derivedSelection.ColumnNullability?[ci] ?? true, spelledNumeric: derivedSelection.ColumnReportsNumeric is { } derivedNumeric && derivedNumeric[ci])
                    {
                        IsUntypedNull = derivedSelection.ColumnIsUntypedNull is { } derivedNulls && derivedNulls[ci],
                        AliasType = derivedSelection.ColumnAliasTypes?[ci],
                        IdentitySource = derivedSelection.ColumnIdentitySources?[ci],
                        DerivedMask = derivedSelection.ColumnMasks?[ci],
                    };
                }

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
                // mandatory: real reports Msg 102 near the token where it
                // belongs, the closing ')' when the batch ends there (probed
                // 2026-09-26: `FROM (SELECT 1 a);` is near ';').
                var derivedQualifier = ConsumeOptionalAlias(context)
                    ?? throw SimulatedSqlException.SyntaxErrorNear(context);
                // A FOR JSON / FOR XML document's column name belongs to the
                // client result; read as a derived table it is unnamed
                // (Msg 8155, probed 2026-10-02 against SQL Server 2025).
                var derivedNames = ResolveDerivedTableColumnNames(context, derivedSelection.IsForClauseDocument ? [""] : derivedSelection.ColumnNames, derivedQualifier);
                // A derived table takes no TABLESAMPLE; real stops at the
                // keyword itself (Msg 156, probed 2026-09-24).
                if (context.Token is ReservedKeyword { Keyword: Keyword.TableSample } tableSample)
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(tableSample);

                return new FromSource(
                    qualifier: derivedQualifier,
                    columnNames: derivedNames,
                    columns: derivedColumns,
                    storedSchema: derivedColumns,
                    storageOrdinals: null,
                    lobStore: null,
                    rows: [],
                    lateralPlan: derivedSelection,
                    lateralIsQueryBody: true,
                    derivedTable: new DerivedTableBinding(derivedSelection, derivedQualifier, derivedNames, context.CurrentDatabase));

            case ReservedKeyword { Keyword: Keyword.OpenXml }:
                // OPENXML dispatch: the pre-OPENJSON XML rowset, read over a
                // document sp_xml_preparedocument put in the session's store.
                // OPENXML is a reserved keyword, so it arrives here rather than
                // in the Name case that carries OPENJSON. ParseOpenXml consumes
                // the argument list and the optional WITH clause, leaving the
                // cursor one past the source (BuiltInRowsetSource's contract).
                return BuiltInRowsetSource(context, ParseOpenXml(context));

            // A schema-bound body may reach none of the three ad hoc rowsets,
            // refused as the parser meets the keyword (probed 2026-10-04
            // against SQL Server 2025).
            case ReservedKeyword { Keyword: Keyword.OpenQuery or Keyword.OpenRowSet or Keyword.OpenDataSource } when context.SchemaBoundBody != SchemaBoundBody.None:
                throw SimulatedSqlException.SyntaxNotAllowedInSchemaBoundObject("Openrowset/Openquery/Opendatasource", 3);

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
                    // The alias an UPDATE or DELETE named as its target makes
                    // this OPENQUERY the statement's write target.
                    if (context.Batch.CurrentStatement.RemoteWriteAlias is { } openQueryTargetAlias)
                    {
                        var targetCheckpoint = context.SaveCheckpoint();
                        var (targetServer, targetQuery) = ParseOpenQueryArguments(context);
                        context.MoveNextOptional();
                        if (ConsumeOptionalAliasAtCurrent(context) is { } writtenTargetAlias && context.CurrentDatabase.Collation.Equals(writtenTargetAlias, openQueryTargetAlias))
                        {
                            var openQueryTarget = RemoteWrite.ForOpenQuery(context.Batch, targetServer, targetQuery, context.Batch.CurrentStatement.RemoteWriteAliasKind);
                            return RemoteTargetSource(context, openQueryTarget, writtenTargetAlias, openQueryTarget.Proxy.Name);
                        }
                        context.RestoreCheckpoint(targetCheckpoint);
                    }

                    var openQueryPlan = ParseOpenQuery(context);
                    context.RemoteSourcesParsed++;
                    var openQueryAlias = ConsumeOptionalAliasAtCurrent(context);
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

            // OPENROWSET: a data file through BULK, or an ad hoc provider
            // rowset — see Selection.OpenRowset.cs.
            case ReservedKeyword { Keyword: Keyword.OpenRowSet }:
                return ParseOpenRowsetSource(context);

            case ReservedKeyword { Keyword: Keyword.OpenDataSource }:
                throw ParseOpenDataSource(context);

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
    /// Whether a name is one of the <c>sys.</c>-qualified system TVFs the FROM
    /// clause dispatches by name, which an APPLY routes the way it routes a
    /// user TVF — the monitoring shape
    /// <c>CROSS APPLY sys.dm_exec_sql_text(r.sql_handle)</c>.
    /// </summary>
    private static bool IsSysRowsetFunction(MultiPartName name) =>
        name.Count == 2
        && BuiltInToken.Equals(name.ImmediateQualifier, "sys")
        && BuiltInToken.EqualsAny(name.Leaf, "dm_exec_cursors", "dm_exec_describe_first_result_set", "dm_exec_input_buffer", "dm_exec_sql_text", "dm_fts_parser", "dm_sql_referenced_entities", "dm_sql_referencing_entities", "fn_virtualfilestats");

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
            columns[ci] = new HeapColumn(plan.ColumnNames[ci], plan.Schema[ci], maxLength: null, nullable: plan.ColumnNullability?[ci] ?? true);
        return new FromSource(
            qualifier: ConsumeOptionalAliasAtCurrent(context),
            columnNames: plan.ColumnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan)
        {
            ConstructsRows = true,
        };
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
        // closing ')'. A subquery in a cell reads the same scope a bare cell
        // reference does — under APPLY, the left side.
        List<Expression[]> tuples;
        // A VALUES derived table is a nested query to the sequence refusals
        // (Msg 11719, probed 2026-10-04 against SQL Server 2025).
        using (ParserScope.Enter(ref context.OuterTypeResolver, outerTypeResolver ?? context.OuterTypeResolver))
        using (context.EnterNextValueForScope(NextValueForScope.Nested))
        {
            tuples = Simulation.ParseValuesTuples(context);
        }
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
        // No TABLESAMPLE here either, as for a query derived table.
        if (context.Token is ReservedKeyword { Keyword: Keyword.TableSample } tableSample)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(tableSample);

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
        // Real unifies the rows as a UNION ALL would, so an untyped NULL cell
        // yields to its typed siblings and an integer literal sizes against a
        // decimal one (probe-confirmed 2026-09-23: `(VALUES ('a'), (NULL))`
        // is varchar, `(VALUES (1), (2.5))` numeric(2, 1)).
        var schema = new SqlType[arity];
        var cells = new (SqlType, int, Expression)[tuples.Count];
        var untypedNull = new bool[arity];
        for (var c = 0; c < arity; c++)
        {
            var count = 0;
            foreach (var tuple in tuples)
            {
                var cell = tuple[c];
                if (!Expression.IsUntypedNullLiteral(cell))
                    cells[count++] = (cell.GetSqlType(context.Batch, TypeResolver), Expression.IntegerLiteralDigits(cell), cell);
            }
            schema[c] = SqlType.PromoteBranches(cells.AsSpan(0, count));
            // The rows settle one collation per column as a UNION ALL's
            // branches do, so two that disagree refuse or leave it unresolved
            // (probed 2026-10-02 against SQL Server 2025: two explicit
            // COLLATE cells are Msg 468 naming UNION ALL).
            if (schema[c].Category == SqlTypeCategory.String)
            {
                SqlType? settled = null;
                for (var i = 0; i < count; i++)
                {
                    var cellType = cells[i].Item1;
                    if (cellType.Category == SqlTypeCategory.String)
                        settled = settled is null ? cellType : UnresolvedCollation.Settle(schema[c], settled, cellType, "UNION ALL");
                }
                if (settled is not null && UnresolvedCollation.On(settled) is { } unresolved)
                    schema[c] = unresolved.Mark(schema[c]);
            }
            untypedNull[c] = count == 0;
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
            // The first decimal-family row names the column, as the first such
            // branch of a set operation does (probed 2026-09-24).
            var spelledNumeric = false;
            if (schema[c] is DecimalSqlType)
            {
                foreach (var tuple in tuples)
                {
                    if (tuple[c].GetSqlType(context.Batch, TypeResolver) is DecimalSqlType)
                    {
                        spelledNumeric = tuple[c].ResultReportsNumeric;
                        break;
                    }
                }
            }
            // A cell reading an enclosing query's masked column masks the
            // column as a set operation's branches do (probed 2026-09-29
            // against SQL Server 2025: `(VALUES (t.s), ('z')) x(v)`).
            DataMask? cellMask = null;
            if (context.OuterMaskResolver is { } outerMask && context.Batch.Connection.Simulation.DeclaresDataMasks)
            {
                foreach (var tuple in tuples)
                    cellMask = DataMask.Merge(cellMask, DataMask.Of(tuple[c], outerMask, cell => cell.GetSqlType(context.Batch, TypeResolver)));
            }
            columns[c] = new HeapColumn(columnNames[c], schema[c], maxLength: null, nullable: nullable, spelledNumeric: spelledNumeric)
            {
                IsUntypedNull = untypedNull[c],
                DerivedMask = cellMask,
            };
        }

        return new FromSource(
            qualifier: alias,
            columnNames: columnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: ForValuesConstructor(schema, columnNames, tuples))
        {
            ConstructsRows = true,
            ConstructorReadsOuterRow = tuples.Exists(static tuple => Array.Exists(tuple, static cell => cell.ReadsAnyColumn())),
        };
    }


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
            if (unnamed is not null)
            {
                // Real reports it and binds on: the unnamed columns just can't
                // be referenced (probed 2026-09-27: `SELECT x1 FROM (SELECT 1)
                // d` is Msg 8155 then Msg 207).
                if (context.Batch.BindErrors is not { } report || (context.Token is not null && !report.Covers(context.Token)))
                    throw SimulatedSqlException.NoColumnNamesSpecified(unnamed, qualifier);
                report.Record(SimulatedSqlException.NoColumnNamesSpecified(unnamed, qualifier), context.Token?.StartIndex ?? (report.Command.Length - 1));
                return projectedNames;
            }
            // The projection's own names must be distinct too (probed
            // 2026-09-24: `(SELECT 1 x, 2 x) d` is Msg 8156).
            return RejectRepeatedColumnName(projectedNames, qualifier);
        }

        var renamed = ParseColumnAliasList(context);
        if (projectedNames.Length > renamed.Length)
            throw SimulatedSqlException.HasMoreColumnsThanColumnList(qualifier);
        if (projectedNames.Length < renamed.Length)
            throw SimulatedSqlException.HasFewerColumnsThanColumnList(qualifier);
        return RejectRepeatedColumnName(renamed, qualifier);
    }

    /// <summary>Returns <paramref name="names"/>, raising Msg 8156 for the first one repeated.</summary>
    private static string[] RejectRepeatedColumnName(string[] names, string qualifier)
    {
        for (var i = 0; i < names.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (Collation.Baseline.Equals(names[i], names[j]))
                    throw SimulatedSqlException.ColumnSpecifiedMultipleTimes(names[i], qualifier);
            }
        }
        return names;
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
    /// Consumes the optional inline join-algorithm hint that may sit between a
    /// join type and <c>JOIN</c> — <c>INNER MERGE JOIN</c>,
    /// <c>LEFT OUTER HASH JOIN</c>, <c>FULL LOOP JOIN</c>. Accept-and-discard:
    /// the hint names the physical operator real should use, and the simulator
    /// picks its own strategy, so it cannot change an answer (probe-confirmed —
    /// hinted and unhinted forms return identical rows).
    /// </summary>
    /// <remarks>
    /// Real accepts all four hints against every join type, including the
    /// combinations that look implausible (<c>FULL LOOP JOIN</c> is legal), and
    /// requires the type keyword: a bare <c>HASH JOIN</c> or <c>MERGE JOIN</c>
    /// is refused, and so is <c>CROSS MERGE JOIN</c>, which is why neither the
    /// CROSS arm nor the bare-<c>JOIN</c> arm calls this. A word here that
    /// isn't a hint is Msg 155 rather than the generic syntax error, and a
    /// second hint is Msg 102 on the second one.
    /// </remarks>
    private static void ConsumeOptionalJoinHint(ParserContext context)
    {
        if (!IsJoinHint(context.Token))
        {
            // Only a bare identifier reaches Msg 155; a reserved keyword here
            // is the ordinary "that isn't JOIN" syntax error the caller raises.
            if (context.Token is UnquotedString word)
                throw SimulatedSqlException.NotARecognizedJoinOption(word.Value);
            return;
        }
        context.MoveNextRequired();
        if (IsJoinHint(context.Token))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // A hint fixes the join order, which real reports with Msg 8625 as
        // the statement compiles (probed 2026-10-01 against SQL Server 2025).
        if (!context.Batch.IsSkipping)
            context.Batch.AppendInfoError(@class: 0, state: 0, SimulatedSqlException.JoinOrderEnforcedMessageNumber, SimulatedSqlException.JoinOrderEnforcedMessage);
    }

    private static bool IsJoinHint(Token? token) => token switch
    {
        ReservedKeyword { Keyword: Keyword.Merge } => true,
        UnquotedString word => BuiltInToken.Equals(word.Value, "HASH")
            || BuiltInToken.Equals(word.Value, "LOOP")
            || BuiltInToken.Equals(word.Value, "REMOTE"),
        _ => false,
    };

    /// <summary>
    /// If <see cref="ParserContext.Token"/> is one of the JOIN-introducing
    /// keywords (<c>INNER</c> / <c>LEFT</c> / <c>RIGHT</c> / <c>FULL</c> /
    /// <c>CROSS</c> / bare <c>JOIN</c>), consumes it (plus an optional
    /// <c>OUTER</c> after LEFT/RIGHT/FULL, an optional join-algorithm hint,
    /// and the required <c>JOIN</c> keyword) and returns the join kind.
    /// Returns false otherwise (no advancement).
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
                ConsumeOptionalJoinHint(context);
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
                ConsumeOptionalJoinHint(context);
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.RecursiveBranchConstructs.OuterJoin = true;
                kind = JoinKind.Left;
                return true;

            case Keyword.Right:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                ConsumeOptionalJoinHint(context);
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.RecursiveBranchConstructs.OuterJoin = true;
                kind = JoinKind.Right;
                return true;

            case Keyword.Full:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                ConsumeOptionalJoinHint(context);
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
                // CROSS takes no join-algorithm hint (real refuses
                // `CROSS MERGE JOIN`), and names the offending word as a
                // keyword when it is one — MERGE being the case that reaches
                // here in practice.
                throw context.Token is ReservedKeyword crossKeyword
                    ? SimulatedSqlException.SyntaxErrorNearKeyword(crossKeyword)
                    : SimulatedSqlException.SyntaxErrorNear(context);

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
        if (nextToken is Name aliasName && !IsWindowClauseAhead(context) && nextToken is not UnquotedString { IsLabelDeclaration: true })
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
        if (context.Token is Name aliasName && !IsWindowClauseAhead(context) && context.Token is not UnquotedString { IsLabelDeclaration: true })
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
}
