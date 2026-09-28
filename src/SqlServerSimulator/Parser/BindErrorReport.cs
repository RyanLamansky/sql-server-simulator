using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The clause a binder error belongs to, in the order real's binder visits
/// them. Only the relative order of clauses one statement kind uses matters:
/// a <c>SELECT</c> binds <c>FROM</c> (its joins' <c>ON</c> and derived tables
/// included), <c>WHERE</c>, <c>GROUP BY</c>, <c>HAVING</c>, the select list,
/// <c>ORDER BY</c>, then <c>TOP</c>; an <c>UPDATE</c> its <c>FROM</c> and
/// <c>WHERE</c>, then the <c>SET</c> targets, the <c>SET</c> values and
/// <c>OUTPUT</c>; an <c>INSERT</c> its source before its column list; a
/// <c>MERGE</c> its source, <c>ON</c>, the insert column list, every
/// <c>WHEN</c> condition and then every action. An <c>INSERT … VALUES</c>
/// arity refusal follows everything (probed 2026-09-27 against SQL Server
/// 2025).
/// </summary>
internal enum BindClause : byte
{
    Leading,
    CteDefinitions,
    MainStatement,
    From,
    Where,
    GroupBy,
    Having,
    SelectList,
    OrderBy,
    Top,
    SetTarget,
    SetValue,
    InsertSource,
    InsertColumns,
    MergeSource,
    MergeOn,
    MergeInsertColumns,
    MergeCondition,
    MergeNotMatchedCondition,
    MergeBySourceCondition,
    MergeAction,
    Assignment,
    Output,
    InsertArity,
}

/// <summary>
/// Every binder error one statement raises, gathered the way real reports
/// them: once per unbindable reference, in the binder's clause order rather
/// than the written one, each at its own line. Exists only while
/// <c>Simulation.ReportEveryBindError</c> re-reads a statement whose first
/// bind failed — the parse that meets a miss here records it and carries on
/// with a stand-in type instead of throwing — so a statement that binds never
/// allocates one.
/// </summary>
/// <remarks>
/// Order is positional. Every query block the re-read parses registers a
/// <see cref="Scope"/> spanning its text, with the offset each clause starts
/// at; an error's sort key is the chain of (clause, offset) pairs from the
/// outermost scope holding it to the innermost, so a subquery's errors land
/// where its text sits inside its parent's clause, and the clauses of one block
/// sort by <see cref="BindClause"/> rather than by where they were written.
/// </remarks>
internal sealed class BindErrorReport(string command)
{
    /// <summary>
    /// Whether <paramref name="error"/> is one real reports among the rest of
    /// its statement's binder errors — the name-resolution, grouping and
    /// aggregate-placement family and an illegal conversion — so that meeting
    /// it first is worth reading the statement again for the whole report.
    /// </summary>
    public static bool StartsReport(SimulatedSqlException error) =>
        error.Number is 109 or 110 or 130 or 147 or 157 or 206 or 207 or 209 or 213 or 257 or 273 or 402 or 468 or 529 or 1011 or 1012 or 1013 or 1015
            or 4101 or 4104 or 5310 or 5319 or 5333 or 5334 or 8116 or 8117 or 8120 or 8121 or 8124 or 8127 or 8155 or 10709 or 13536;

    /// <summary>
    /// Whether <paramref name="error"/> is a type check real reports where it
    /// sits among a statement's binder errors and binds on past: an operand
    /// type clash, incompatible operand types, an implicit conversion or
    /// collation it can't settle, an operand or argument type an operator or
    /// function refuses. Each reports only while nothing ahead of it has
    /// failed, so a statement reports at most one (probed 2026-09-28 against
    /// SQL Server 2025: <c>SELECT a + d, x1</c> is Msg 206 then Msg 207,
    /// <c>SELECT x1, a + d</c> the Msg 207 alone, <c>SELECT a + d, a + d</c> one
    /// Msg 206). An illegal explicit conversion (Msg 529) is the exception real
    /// stops binding at; see <see cref="RecordTypeError"/>.
    /// </summary>
    public static bool IsTypeCheck(SimulatedSqlException error) =>
        error.Number is 206 or 257 or 402 or 468 or 8116 or 8117;

    /// <summary>
    /// Whether the re-read carries on past <paramref name="error"/>, raised
    /// typing <paramref name="node"/>: an operand whose typing recorded an error
    /// since <paramref name="recordedBefore"/> is error-typed on real, which no
    /// check refuses, and a type check of its own is recorded where the node's
    /// first column reference sits. A node reading no column keeps throwing.
    /// </summary>
    public bool CarriesPastTypeCheck(SimulatedSqlException error, int recordedBefore, ExpressionNode node)
    {
        if (!IsTypeCheck(error))
            return false;
        if (this.Count > recordedBefore)
            return true;
        if (this.SpanOf(node) is not { } span)
            return false;
        // A term typed again (for its nullability, say) meets its operands'
        // errors, and its own check, already recorded.
        if (!this.entries.Exists(entry => span.Start <= entry.LinePosition && entry.LinePosition < span.End))
            this.RecordUnlessPreceded(error, span.Start);
        return true;
    }

    /// <summary>The text whose offsets every recorded position is into.</summary>
    public readonly string Command = command;

    private readonly List<Entry> entries = [];
    private readonly List<Scope> scopes = [];
    private readonly List<Scope> openScopes = [];

    private struct Entry
    {
        public SimulatedSqlException Error;

        /// <summary>Where the error sorts; for a grouping violation, the end of its expression.</summary>
        public int SortPosition;

        /// <summary>Breaks a tie at one position: the expression's own name errors first, then its grouping verdict.</summary>
        public int Tier;

        /// <summary>Orders a deferred error after the position it was deferred to; see <see cref="Defer"/>.</summary>
        public int Sub;

        /// <summary>The position whose line the error reports.</summary>
        public int LinePosition;

        /// <summary>A clause that overrides the one <see cref="SortPosition"/> falls in, for errors whose clause isn't a text span (an <c>UPDATE</c>'s <c>SET</c> targets).</summary>
        public BindClause? Clause;

        /// <summary>
        /// For a check real makes only while nothing ahead of it has failed —
        /// a grouping violation, from the start of its expression; Msg 130 and
        /// 147, from where they sit — the position a kept error sorting ahead
        /// of drops it. -1 for an error reported unconditionally.
        /// </summary>
        public int EligibleFrom;

        /// <summary>
        /// A type check: reported alone when nothing sorts ahead of it, and
        /// skipped otherwise — real stops binding at one it meets first and
        /// doesn't make one it would meet after an error.
        /// </summary>
        public bool Alone;

        /// <summary>A FROM clause's name collision, after which real binds nothing more.</summary>
        public bool Ends;

        /// <summary>
        /// An error real meets binding its query's aggregates, which it does
        /// ahead of judging that query's grouping: no grouping violation of the
        /// same query reports beside it, wherever each is written.
        /// </summary>
        public bool PrecedesGrouping;
    }

    /// <summary>How many errors have been recorded, so a caller can tell whether its operand's typing failed.</summary>
    public int Count => this.entries.Count;

    private sealed class Scope(int start)
    {
        public readonly int Start = start;
        public int End = int.MaxValue;
        public readonly List<(int Start, BindClause Clause)> Clauses = [];

        /// <summary>
        /// Clauses real stops binding at when an earlier one failed: an
        /// error before a barrier drops every error at or after it.
        /// </summary>
        public BindClause[]? Barriers;

        public BindClause ClauseAt(int position)
        {
            var clause = BindClause.Leading;
            var best = -1;
            foreach (var (start, candidate) in this.Clauses)
            {
                if (start <= position && start > best)
                    (best, clause) = (start, candidate);
            }
            return clause;
        }
    }

    /// <summary>Whether <paramref name="token"/> was read from <see cref="Command"/> — a view's or a module's own text never is.</summary>
    public bool Covers(Token? token) => token is not null && ReferenceEquals(token.command, this.Command);

    /// <summary>
    /// Opens a query block or statement starting at <paramref name="start"/>;
    /// a block the parser reads twice (a speculative pass it discarded) reuses
    /// the scope the first read opened.
    /// </summary>
    public void OpenScope(Token? start)
    {
        if (!this.Covers(start))
        {
            // Keeps Open / Close paired for a block read from another text.
            this.openScopes.Add(new Scope(-1));
            return;
        }
        var scope = this.scopes.Find(existing => existing.Start == start!.StartIndex);
        if (scope is null)
        {
            scope = new Scope(start!.StartIndex);
            this.scopes.Add(scope);
        }
        this.openScopes.Add(scope);
    }

    /// <summary>
    /// Spans a statement's <c>WITH</c> prefix, from <paramref name="with"/> to
    /// the statement it leads at <paramref name="main"/>: real binds every
    /// common table expression, referenced or not, and stops there when one
    /// fails, so the statement's own errors go unreported (probed 2026-09-27).
    /// </summary>
    public void AddCtePrefix(Token with, Token? main)
    {
        if (!this.Covers(with))
            return;
        var scope = new Scope(with.StartIndex) { Barriers = [BindClause.MainStatement] };
        scope.Clauses.Add((with.StartIndex, BindClause.CteDefinitions));
        if (this.Covers(main))
            scope.Clauses.Add((main!.StartIndex, BindClause.MainStatement));
        this.scopes.Add(scope);
    }

    /// <summary>Closes the innermost open scope where the parse stopped.</summary>
    public void CloseScope(Token? end)
    {
        var scope = this.openScopes[^1];
        this.openScopes.RemoveAt(this.openScopes.Count - 1);
        if (scope.Start >= 0)
            scope.End = end is not null && this.Covers(end) ? end.StartIndex : this.Command.Length;
    }

    /// <summary>Marks the innermost open scope's <paramref name="clause"/> as starting at <paramref name="at"/>.</summary>
    public void EnterClause(Token? at, BindClause clause)
    {
        if (this.openScopes.Count == 0 || this.openScopes[^1] is not { Start: >= 0 } scope || !this.Covers(at))
            return;
        var start = at!.StartIndex;
        if (!scope.Clauses.Exists(existing => existing.Start == start))
            scope.Clauses.Add((start, clause));
    }

    /// <summary>Sets the clauses the innermost open scope stops binding at once an earlier one failed.</summary>
    public void SetBarriers(params BindClause[] barriers)
    {
        if (this.openScopes.Count != 0 && this.openScopes[^1] is { Start: >= 0 } scope)
            scope.Barriers = barriers;
    }

    /// <summary>Spans whose errors real reports again elsewhere, and where; see <see cref="Echo"/>.</summary>
    private List<(int Start, int End, int Target)>? echoes;

    /// <summary>Spans whose errors real reports elsewhere instead, and where; see <see cref="Defer"/>.</summary>
    private List<(int Start, int End, int Target, int Sub)>? deferrals;

    /// <summary>
    /// Reports every name error in <paramref name="start"/>..<paramref name="end"/>
    /// a second time, at <paramref name="target"/>: the shapes real binds by
    /// expansion bind an operand once per copy — <c>COALESCE(a, b, c)</c>
    /// as <c>CASE WHEN a IS NOT NULL THEN a WHEN b …</c>, <c>NULLIF</c>,
    /// <c>BETWEEN</c>, a simple <c>CASE</c>'s input and an <c>IN</c> list's
    /// left operand (probed 2026-09-27: <c>COALESCE(x1, x2, x3)</c> reports
    /// x1, x2, x1, x2, x3).
    /// </summary>
    public void Echo(int start, int end, int target) =>
        (this.echoes ??= []).Add((start, end, target));

    /// <summary>
    /// Reports every error in <paramref name="start"/>..<paramref name="end"/>
    /// at <paramref name="target"/> rather than where it was written, after
    /// what <paramref name="target"/> itself holds when <paramref name="sub"/>
    /// is 1: real binds a <c>CASE</c>'s results after all its conditions, a
    /// window function's arguments after its <c>OVER</c> clause, and an
    /// <c>IN</c> list's elements last to first (probed 2026-09-27).
    /// </summary>
    public void Defer(int start, int end, int target, int sub = 0) =>
        (this.deferrals ??= []).Add((start, end, target, sub));

    /// <summary>
    /// The span of <paramref name="expression"/>'s own column references in
    /// <see cref="Command"/> — first reference to just past the last — or
    /// null when it reads none written there.
    /// </summary>
    public (int Start, int End)? SpanOf(ExpressionNode? expression)
    {
        if (expression is null)
            return null;
        var start = int.MaxValue;
        var end = -1;
        expression.Walk((node, _) =>
        {
            if (node is Expressions.Reference { SourceToken: { } token } && this.Covers(token))
            {
                start = Math.Min(start, token.StartIndex);
                end = Math.Max(end, token.StartIndex + 1);
            }
            return true;
        });
        return end < 0 ? null : (start, end);
    }

    /// <summary>Where each aggregate the re-read parsed ends, which places a Msg 147 about it.</summary>
    private Dictionary<Expressions.AggregateExpression, int>? aggregateEnds;

    /// <summary>Notes that <paramref name="aggregate"/> finished parsing at <paramref name="at"/>.</summary>
    public void NoteAggregate(Expressions.AggregateExpression aggregate, Token? at)
    {
        if (this.Covers(at))
            (this.aggregateEnds ??= new(ReferenceEqualityComparer.Instance))[aggregate] = at!.StartIndex;
    }

    /// <summary>
    /// Records <paramref name="error"/> — an aggregate standing where real
    /// refuses one, Msg 147 for a <c>WHERE</c> clause's own — against
    /// <paramref name="aggregate"/>, answering whether it could. Real reports it
    /// where the aggregate ends, and not at all when an unbindable name comes
    /// first — its operand's included (probed 2026-09-27: <c>WHERE COUNT(x1)
    /// &gt; 1</c> is the Msg 207 alone, <c>WHERE SUM(a) &gt; 1 AND x2 = 1</c>
    /// Msg 147 then Msg 207).
    /// </summary>
    public bool RecordAggregatePlacement(SimulatedSqlException error, Expressions.AggregateExpression aggregate)
    {
        if (this.aggregateEnds is null || !this.aggregateEnds.TryGetValue(aggregate, out var end))
            return false;
        // Real binds a query's aggregates before judging its grouping (probed
        // 2026-09-28: a subquery's `ORDER BY MAX(t.a + u.c)` is Msg 8124 alone
        // beside its ungrouped select list).
        this.entries.Add(new Entry
        {
            Error = error,
            SortPosition = end,
            LinePosition = end,
            EligibleFrom = end,
            PrecedesGrouping = error.Number == 8124,
        });
        return true;
    }

    /// <summary>The token each <c>UPDATE … SET</c> value's target was written at, keyed by the value.</summary>
    private Dictionary<Expression, Token>? setTargets;

    /// <summary>Notes that the <c>SET</c> clause assigning <paramref name="value"/> names its target at <paramref name="target"/>.</summary>
    public void NoteSetTarget(Expression value, Token target)
    {
        if (this.Covers(target))
            (this.setTargets ??= new(ReferenceEqualityComparer.Instance))[value] = target;
    }

    /// <summary>
    /// Records <paramref name="error"/> against the <c>SET</c> target of the
    /// clause assigning <paramref name="value"/>, answering whether it could —
    /// false leaves the caller to throw.
    /// </summary>
    public bool RecordSetTarget(SimulatedSqlException error, Expression value)
    {
        if (this.setTargets is null || !this.setTargets.TryGetValue(value, out var target))
            return false;
        this.Record(error, target.StartIndex, BindClause.SetTarget);
        return true;
    }

    /// <summary>Whether a name error was recorded at <paramref name="position"/>.</summary>
    public bool FailedAt(int position)
    {
        foreach (var entry in this.entries)
        {
            if (entry.LinePosition == position && entry.EligibleFrom < 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Records <paramref name="error"/> — a name-resolution miss raised
    /// binding the reference at <paramref name="token"/> — when it is one this
    /// report gathers, answering whether the caller should carry on with a
    /// stand-in rather than throw. A reference the parse types more than once
    /// reports once.
    /// </summary>
    public bool TryRecordNameError(SimulatedSqlException error, Token? token)
    {
        if (error.Number is not (207 or 209 or 4104 or 5333 or 5334) || !this.Covers(token))
            return false;
        if (!this.FailedAt(token!.StartIndex))
            this.Record(error, token.StartIndex);
        return true;
    }

    /// <summary>Records an error the parse carries on past, at <paramref name="position"/>.</summary>
    public void Record(SimulatedSqlException error, int position, BindClause? clause = null) =>
        this.entries.Add(new Entry
        {
            Error = error,
            SortPosition = position,
            LinePosition = position,
            Clause = clause,
            EligibleFrom = -1,
        });

    /// <summary>
    /// Records a value its write target can't take without an explicit
    /// conversion (Msg 206 / 257 and the other type checks), answering whether
    /// it could. Real makes the check once every value of the statement has
    /// bound, and only while nothing failed ahead of it (probed 2026-09-28
    /// against SQL Server 2025: <c>UPDATE t SET a = d, s = x1</c> and
    /// <c>INSERT u (c, e) SELECT d, x1 FROM t</c> report the Msg 207 alone).
    /// </summary>
    public bool TryRecordAssignmentCheck(SimulatedSqlException error, ExpressionNode? value)
    {
        if (!IsTypeCheck(error))
            return false;
        // A value reading no column sorts by its statement, the clause alone
        // placing it.
        var position = this.SpanOf(value)?.Start ?? (this.openScopes.Find(scope => scope.Start >= 0) is { } statement ? statement.Start : -1);
        if (position < 0)
            return false;
        this.entries.Add(new Entry
        {
            Error = error,
            SortPosition = position,
            LinePosition = position,
            Clause = BindClause.Assignment,
            EligibleFrom = position,
        });
        return true;
    }

    /// <summary>
    /// Records an illegal conversion (Msg 529), which real reports alone when
    /// its binder meets it before any other error and skips when it meets it
    /// after one (probed 2026-09-27: <c>SELECT x1, CAST(CAST(1 AS date) AS
    /// int), x2</c> sends the two Msg 207s, while the same conversion in the
    /// <c>WHERE</c> of <c>SELECT x1 …</c> is Msg 529 alone).
    /// </summary>
    public void RecordTypeError(SimulatedSqlException error, int position) =>
        this.entries.Add(new Entry
        {
            Error = error,
            SortPosition = position,
            LinePosition = position,
            EligibleFrom = -1,
            Alone = true,
        });

    /// <summary>
    /// Records a FROM clause's name collision (Msg 1011 / 1012 / 1013) at
    /// <paramref name="position"/>, where real stops binding: what it bound
    /// ahead of the colliding source reports, and nothing after (probed
    /// 2026-09-27: <c>SELECT x1 FROM t JOIN u ON x2 = 1 JOIN u ON x3 = 1 WHERE
    /// x4 = 1</c> reports x2, then Msg 1013).
    /// </summary>
    public void RecordCollision(SimulatedSqlException error, int position) =>
        this.entries.Add(new Entry
        {
            Error = error,
            SortPosition = position,
            LinePosition = position,
            EligibleFrom = -1,
            Ends = true,
        });

    /// <summary>
    /// Records an error real reports only when nothing ahead of it in the
    /// binder's order has failed — a Msg 130 nesting check, which sees an
    /// aggregate over an unbindable column as error-typed and passes it
    /// (probed 2026-09-27: <c>SUM(SUM(x1)), x2</c> reports the two Msg 207s,
    /// <c>SUM(COUNT(*)), x1</c> Msg 130 then Msg 207).
    /// </summary>
    public void RecordUnlessPreceded(SimulatedSqlException error, int position) =>
        this.entries.Add(new Entry
        {
            Error = error,
            SortPosition = position,
            LinePosition = position,
            EligibleFrom = position,
        });

    /// <summary>
    /// Records a GROUP BY containment violation (Msg 8120 / 8121 / 8127) at
    /// <paramref name="position"/> in the expression spanning
    /// <paramref name="expressionStart"/>..<paramref name="expressionEnd"/>.
    /// Real reports it after the expression's own name errors, and only when
    /// no error sorts ahead of the expression (probed 2026-09-27:
    /// <c>SELECT a + x1 … GROUP BY b</c> reports Msg 207 then Msg 8120,
    /// <c>SELECT x1 + a</c> the Msg 207 alone, and <c>SELECT a, b … GROUP BY
    /// c</c> only <c>a</c>).
    /// </summary>
    public void RecordGroupingViolation(SimulatedSqlException error, int position, int expressionStart, int expressionEnd) =>
        this.entries.Add(new Entry
        {
            Error = error,
            SortPosition = expressionEnd,
            Tier = 1,
            LinePosition = position,
            EligibleFrom = expressionStart,
        });

    /// <summary>
    /// The statement's whole report, or null when the re-read gathered
    /// nothing, which leaves the error that started it to stand alone.
    /// </summary>
    /// <param name="stopper">
    /// The error that ended the re-read early, if one did. Real's parse phase
    /// (severity 15) preempts binding and reports alone; a structural error it
    /// meets once names bound follows them; anything else — a type check — is
    /// one real skips once an error is in hand.
    /// </param>
    /// <param name="statementLine">The line every error reports when <paramref name="parameterized"/>.</param>
    /// <param name="parameterized">
    /// Whether real compiles the statement through simple parameterization,
    /// which reports every error at the statement's first line rather than at
    /// the reference's own.
    /// </param>
    /// <param name="lineOffset">Added to a reference's own line, for a body read from text of its own.</param>
    public SimulatedSqlException? Build(SimulatedSqlException? stopper, int statementLine, bool parameterized, int lineOffset = 0)
    {
        if (stopper is { Class: <= 15 } && stopper.Number is not (130 or 147 or 1087))
            return stopper;
        if (this.entries.Count == 0)
            return null;

        var placed = new List<Entry>(this.entries.Count);
        foreach (var written in this.entries)
        {
            var entry = written;
            if (entry.EligibleFrom < 0 && !entry.Alone)
            {
                foreach (var (start, end, target, sub) in this.deferrals ?? [])
                {
                    if (start <= entry.LinePosition && entry.LinePosition < end)
                        (entry.SortPosition, entry.Sub) = (target, sub);
                }
                foreach (var (start, end, target) in this.echoes ?? [])
                {
                    if (start <= entry.LinePosition && entry.LinePosition < end)
                        placed.Add(entry with { Error = entry.Error.CopyOfError(), SortPosition = target, Sub = 0 });
                }
            }
            placed.Add(entry);
        }

        var keyed = new List<(List<(int Rank, int Anchor)> Key, Entry Entry, int Sequence)>(placed.Count);
        for (var i = 0; i < placed.Count; i++)
            keyed.Add((this.KeyOf(placed[i].SortPosition, placed[i].Clause), placed[i], i));
        keyed.Sort((x, y) =>
        {
            var byKey = CompareKeys(x.Key, y.Key);
            if (byKey != 0)
                return byKey;
            var bySub = x.Entry.Sub.CompareTo(y.Entry.Sub);
            if (bySub != 0)
                return bySub;
            var byTier = x.Entry.Tier.CompareTo(y.Entry.Tier);
            if (byTier != 0)
                return byTier;
            var byWritten = x.Entry.LinePosition.CompareTo(y.Entry.LinePosition);
            return byWritten != 0 ? byWritten : x.Sequence.CompareTo(y.Sequence);
        });

        // A barrier: an error in one of its scope's earlier clauses stops the
        // binder before the barrier's clause. An entry's rank in a scope is
        // the key component that scope contributed.
        foreach (var scope in this.scopes)
        {
            if (scope.Barriers is not { } barriers)
                continue;
            int RankIn((List<(int Rank, int Anchor)> Key, Entry Entry, int Sequence) item) =>
                this.ChainOf(item.Entry.SortPosition).IndexOf(scope) is var depth and >= 0 ? item.Key[depth + 1].Rank : -1;
            foreach (var barrier in barriers)
            {
                if (keyed.Exists(item => RankIn(item) is >= 0 and var rank && rank < (int)barrier))
                    _ = keyed.RemoveAll(item => RankIn(item) >= (int)barrier);
            }
        }

        var reported = new List<SimulatedSqlException>(keyed.Count + 1);
        List<(int Rank, int Anchor)>? firstKept = null;
        foreach (var (key, entry, _) in keyed)
        {
            if (entry.Tier == 1 && this.ChainOf(entry.SortPosition) is [.., var own] && keyed.Exists(other => other.Entry.PrecedesGrouping && this.ChainOf(other.Entry.SortPosition) is [.., var otherOwn] && otherOwn == own))
                continue;
            if (entry.EligibleFrom >= 0 && firstKept is not null && CompareKeys(firstKept, this.KeyOf(entry.EligibleFrom, entry.Clause)) < 0)
                continue;
            if (entry.Alone)
            {
                if (firstKept is not null)
                    continue;
                // Real places a conversion refusal at the statement's line
                // rather than the conversion's (probed 2026-09-27).
                entry.Error.Errors[0].LineNumber = statementLine;
                entry.Error.BindReportSettled = true;
                return entry.Error;
            }
            firstKept ??= key;
            entry.Error.Errors[0].LineNumber = parameterized ? statementLine : Token.LineAt(this.Command, entry.LinePosition) + lineOffset;
            reported.Add(entry.Error);
            if (entry.Ends)
                break;
        }

        // What the stopper carries follows, when it is more of the same: an
        // unbindable name the parse raised rather than recorded — a second
        // read of one already reported repeats its message — or a structural
        // error real meets once the names have bound.
        if (stopper is not null)
        {
            var gathered = stopper.Errors.Count > 1;
            foreach (var error in stopper.Errors)
            {
                if (error.Number is 104 or 207 or 209 or 1011 or 1012 or 1013 or 4104 or 5333 or 5334
                    && (gathered || !reported.Exists(existing => existing.Number == error.Number && existing.Message == error.Message)))
                {
                    reported.Add(SimulatedSqlException.FromErrors([error]));
                }
            }
        }
        var whole = SimulatedSqlException.Aggregate(reported);
        whole.BindReportSettled = true;
        whole.CatchReadsFirstEntry = true;
        return whole;
    }

    /// <summary>The scopes holding <paramref name="position"/>, outermost first.</summary>
    private List<Scope> ChainOf(int position)
    {
        var chain = this.scopes.FindAll(scope => scope.Start <= position && position < scope.End);
        chain.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : y.End.CompareTo(x.End));
        return chain;
    }

    /// <summary>
    /// The sort key of <paramref name="position"/>: a leading (0, anchor)
    /// pair for the statement, then per scope holding it, outermost first,
    /// the clause and offset of what it holds next — a nested scope's start,
    /// or the position itself in the innermost.
    /// </summary>
    private List<(int Rank, int Anchor)> KeyOf(int position, BindClause? clause)
    {
        var chain = this.ChainOf(position);
        var key = new List<(int, int)>(chain.Count + 1) { (0, chain.Count > 0 ? chain[0].Start : position) };
        for (var i = 0; i < chain.Count; i++)
        {
            var anchor = i + 1 < chain.Count ? chain[i + 1].Start : position;
            var rank = i + 1 == chain.Count && clause is { } overridden ? overridden : chain[i].ClauseAt(anchor);
            key.Add(((int)rank, anchor));
        }
        return key;
    }

    private static int CompareKeys(List<(int Rank, int Anchor)> x, List<(int Rank, int Anchor)> y)
    {
        for (var i = 0; i < x.Count && i < y.Count; i++)
        {
            var byRank = x[i].Rank.CompareTo(y[i].Rank);
            if (byRank != 0)
                return byRank;
            var byAnchor = x[i].Anchor.CompareTo(y[i].Anchor);
            if (byAnchor != 0)
                return byAnchor;
        }
        return x.Count.CompareTo(y.Count);
    }

    /// <summary>
    /// Whether real compiles the statement spelled by <paramref name="tokens"/>
    /// through simple parameterization, whose errors all report the statement's
    /// first line: a single-table <c>SELECT</c> / <c>UPDATE</c> /
    /// <c>DELETE</c> / <c>INSERT</c> with no join, subquery, set operator,
    /// grouping, <c>DISTINCT</c>, <c>TOP</c>, <c>INTO</c>, window, <c>OPTION</c>,
    /// <c>OUTPUT</c>, <c>IN</c> list, <c>LIKE</c>, <c>OR</c> or variable, carrying a
    /// literal it can parameterize — one in a <c>SELECT</c>'s or
    /// <c>DELETE</c>'s <c>WHERE</c>, an <c>UPDATE</c>'s <c>SET</c> or
    /// <c>WHERE</c>, or an <c>INSERT</c>'s <c>VALUES</c> or FROM-less
    /// <c>SELECT</c> — an <c>INSERT … SELECT … FROM</c> isn't. A select-list literal
    /// doesn't count (probed 2026-09-27 against SQL Server 2025; a module body
    /// is never parameterized, which the caller settles).
    /// </summary>
    public static bool IsSimplyParameterizable(List<Token> tokens)
    {
        if (tokens.Count == 0 || tokens[0] is not ReservedKeyword { Keyword: Keyword.Select or Keyword.Update or Keyword.Delete or Keyword.Insert } lead)
            return false;

        var literalRegion = false;
        var literalSeen = false;
        var selects = lead.Keyword == Keyword.Select ? 1 : 0;
        var froms = 0;
        var inFrom = false;
        var depth = 0;
        for (var i = 1; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case ReservedKeyword
                {
                    Keyword: Keyword.Join or Keyword.Cross or Keyword.Union or Keyword.Except or Keyword.Intersect
                        or Keyword.Group or Keyword.Having or Keyword.Distinct or Keyword.Top or Keyword.Option or Keyword.Over
                        or Keyword.In or Keyword.Like or Keyword.Or or Keyword.Exists or Keyword.Pivot or Keyword.Unpivot
                }:
                    return false;
                case ReservedKeyword { Keyword: Keyword.Into } when lead.Keyword != Keyword.Insert:
                    return false;
                case ReservedKeyword { Keyword: Keyword.Select }:
                    if (++selects > 1 || lead.Keyword is Keyword.Update or Keyword.Delete)
                        return false;
                    literalRegion |= lead.Keyword == Keyword.Insert;
                    break;
                case ReservedKeyword { Keyword: Keyword.From }:
                    if (++froms > 1 || lead.Keyword is Keyword.Update or Keyword.Insert)
                        return false;
                    inFrom = true;
                    break;
                case ReservedKeyword { Keyword: Keyword.Where }:
                    inFrom = false;
                    literalRegion = true;
                    break;
                case ReservedKeyword { Keyword: Keyword.Set } when lead.Keyword == Keyword.Update:
                case ReservedKeyword { Keyword: Keyword.Values } when lead.Keyword == Keyword.Insert:
                    literalRegion = true;
                    break;
                case ReservedKeyword { Keyword: Keyword.Order }:
                    inFrom = false;
                    break;
                case UnquotedString { Span: var word } when word.Equals("OUTPUT", StringComparison.OrdinalIgnoreCase) || word.Equals("APPLY", StringComparison.OrdinalIgnoreCase):
                case AtPrefixedString:
                    return false;
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: ',' } when inFrom && depth == 0:
                    return false;
                case Literal or Numeric:
                    literalSeen |= literalRegion;
                    break;
            }
        }
        return literalSeen;
    }
}
