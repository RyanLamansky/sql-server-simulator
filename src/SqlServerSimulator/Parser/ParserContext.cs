using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Organizes relevant information for parsing of SQL commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lookahead contract.</b> Every <c>Parse</c>-style helper in this
/// namespace (e.g. <see cref="Expression.Parse(ParserContext)"/>,
/// <see cref="Selection.Parse"/>,
/// <see cref="BooleanExpression.Parse"/>) leaves <see cref="Token"/> at the
/// first token it did <i>not</i> consume — its caller's lookahead position.
/// A helper that reads up to and including a closing delimiter (e.g. a
/// function call's <c>)</c>) leaves <see cref="Token"/> on that delimiter;
/// the surrounding loop's next <see cref="GetNextOptional"/> /
/// <see cref="MoveNext"/> advances past it. Callers must not "step back" or
/// "step forward" to re-align after a Parse returns.
/// </para>
/// <para>
/// This contract is what makes recursive descent compose. Violations show up
/// as silently dropped tokens. When in doubt, read a token at the call site,
/// decide whether to consume it, and never assume a previous Parse left the
/// cursor "before" or "after" something the contract didn't promise.
/// </para>
/// </remarks>
internal sealed class ParserContext(SimulatedDbCommand command, BatchContext batch)
{
#pragma warning disable CA2213 // Disposable fields should be disposed
    public readonly SimulatedDbCommand Command = command;
#pragma warning restore CA2213 // Suppressed because ParserContext doesn't own the command object.

    /// <summary>
    /// The owning batch's runtime state (variable slots, undo log). Parsers
    /// route runtime concerns through this backreference; the parser context
    /// itself holds only parse-time scratch (tokenizer cursor, collectors,
    /// outer-type resolver).
    /// </summary>
    public readonly BatchContext Batch = batch;

    private readonly string commandText = string.IsNullOrEmpty(command.CommandText) ?
        throw new InvalidOperationException("ExecuteReader: CommandText property has not been initialized") :
        command.CommandText;

    /// <summary>
    /// The tokenizer position within <see cref="commandText"/>: the next
    /// un-read character. <see cref="MoveNext"/> advances this past the
    /// returned token (see <see cref="Tokenizer"/>'s index contract).
    /// </summary>
    private int index;

    /// <summary>
    /// The effective <c>QUOTED_IDENTIFIER</c> setting at the current parse
    /// position: <see langword="true"/> tokenizes <c>"…"</c> as a delimited
    /// identifier, <see langword="false"/> as a varchar string literal.
    /// Seeded from the session (<c>SET QUOTED_IDENTIFIER</c> persists across
    /// batches); flipped mid-batch by the SET parser at the statement's
    /// textual position regardless of control flow — SQL Server applies this
    /// option at parse time, so a SET inside a never-taken IF branch still
    /// affects everything after it in the batch (probe-confirmed).
    /// </summary>
    public bool QuotedIdentifiers = command.Connection!.QuotedIdentifiers;

    /// <summary>
    /// Live weighted nesting budget shared by grouped-expression parens,
    /// scalar subqueries, and function-call argument lists — the constructs
    /// SQL Server pools into one "nested too deeply" limit (probe-confirmed
    /// 2026-07-18: nesting them together fails on a single shared budget, a
    /// subquery level costing roughly six paren levels). Each construct's
    /// parser adds its cost on entry and subtracts it in a <c>finally</c>;
    /// crossing <c>Expression.MaxNestingDepth</c> raises Msg 191. The
    /// companion stack-probe guard at <see cref="Expression.Parse"/> entry
    /// raises Msg 8631 when actual remaining stack runs low first.
    /// </summary>
    public int NestingDepth;

    /// <summary>
    /// Live lexical nesting depth of <c>CASE</c> / <c>IIF</c> expressions,
    /// incremented on entry and decremented in a <c>finally</c> by their
    /// parsers. SQL Server caps this at <see cref="MaxCaseNestingDepth"/>
    /// (Msg 125) and counts nesting in any child position — <c>WHEN</c>
    /// condition, <c>THEN</c> / <c>ELSE</c> result — and does not reset the
    /// count across a scalar-subquery boundary (probe-confirmed 2026-07-18),
    /// so the counter lives on the shared context rather than resetting per
    /// nested SELECT.
    /// </summary>
    public int CaseDepth;

    /// <summary>
    /// Whether every expression parsed so far inside the innermost
    /// constant-foldable construct — a call to one of
    /// <see cref="ConstantFolding.IsFoldedBuiltIn"/>'s built-ins, or a
    /// <c>CASE</c> — has been a written constant. The construct's parser sets
    /// it on entry, reads it to decide whether the node folds, and restores
    /// the enclosing construct's value in a <c>finally</c>;
    /// <see cref="Expression.Parse"/> clears it for a non-constant return.
    /// False outside any such construct.
    /// </summary>
    public bool FoldableArguments;

    /// <summary>
    /// SQL Server's fixed <c>CASE</c> / <c>IIF</c> lexical-nesting cap: ten
    /// levels succeed, an eleventh raises Msg 125 ("Case expressions may only
    /// be nested to level 10.").
    /// </summary>
    public const int MaxCaseNestingDepth = 10;

    /// <summary>
    /// The most recently identified token in the command string.
    /// </summary>
    public Token? Token;

    /// <summary>
    /// True while an <c>Expression.Parse</c> call is running for a
    /// <c>CREATE TABLE</c> column's <c>DEFAULT</c> clause. Set by the
    /// CREATE-TABLE parser around the call to
    /// <see cref="Expression.Parse(ParserContext)"/> and cleared in
    /// <c>finally</c>. Built-in functions whose grammar restricts them to
    /// DEFAULT clauses (currently <c>NEWSEQUENTIALID</c>) inspect this flag
    /// and raise Msg 302 when it isn't set.
    /// </summary>
    public bool InDefaultClause;

    /// <summary>
    /// When non-null, every <see cref="Expressions.AggregateExpression"/>
    /// constructor registers itself here, letting the surrounding
    /// <see cref="Selection"/> parser learn which aggregates appear in the
    /// projection / HAVING clauses without re-walking the expression trees.
    /// Scoped by Selection.Parse: the outer caller sets the list before
    /// parsing projection / HAVING, then snapshots the collected aggregates
    /// and clears it. Nested SELECT scopes each get their own list.
    /// </summary>
    public List<Expressions.AggregateExpression>? AggregateCollector;

    /// <summary>
    /// The <see cref="AggregateCollector"/> of the query one level out, kept
    /// so an aggregate written inside a nested scope but reading only the
    /// enclosing query's columns can be re-homed to the query that owns those
    /// columns. Null at the outermost SELECT.
    /// </summary>
    public List<Expressions.AggregateExpression>? EnclosingAggregateCollector;

    /// <summary>
    /// Monotonic parse-time occurrence counters, bumped once per node of the
    /// named kind as the parser builds expressions. Bracketing a sub-parse
    /// (snapshot before, compare after) answers "did this expression contain an
    /// aggregate / subquery / column reference?" without walking the finished
    /// tree — which matters because only a minority of the 170-odd
    /// <see cref="Expression"/> subclasses override
    /// <see cref="Expression.VisitColumnReferences"/>, so a tree walk silently
    /// misses containers like <c>CASE</c> and most scalar function calls.
    /// Counting at construction is complete by construction instead.
    /// <para>Consumed by the aggregate-binding rules: Msg 130 (aggregate over an
    /// aggregate or subquery), Msg 144 (aggregate / subquery in a GROUP BY
    /// item) and Msg 164 (GROUP BY item with no column of its own). Deltas are
    /// only meaningful across a single sub-parse on one context — never read the
    /// absolute values.</para>
    /// <para>These deliberately do <b>not</b> reset per statement: every consumer
    /// compares two snapshots taken around the same parse, so only the
    /// difference is load-bearing, and a shared monotonic counter avoids any
    /// save/restore discipline at nested-SELECT boundaries.</para>
    /// </summary>
    public int AggregatesParsed;

    /// <inheritdoc cref="AggregatesParsed"/>
    public int SubqueriesParsed;

    /// <summary>
    /// Column references parsed, net of function names. A bare name is built as
    /// a <see cref="Expressions.Reference"/> before the parser knows whether a
    /// <c>(</c> follows, so <c>GETDATE()</c> starts life looking exactly like a
    /// column; <c>Expression.ParseCallArguments</c> — the single funnel for
    /// every <c>&lt;reference&gt;(</c> shape — decrements on entry to cancel
    /// that. The delta across a sub-parse is therefore the count of *genuine*
    /// column references, which is what Msg 164 needs.
    /// </summary>
    /// <remarks>
    /// An <em>outer</em> column reference counts the same as a local one, so a
    /// grouping item naming only an outer column (<c>GROUP BY o.a</c> inside a
    /// correlated subquery) stays accepted where real raises Msg 164. That
    /// residual is the permissive direction and matches the pre-existing
    /// behavior; closing it needs source-resolution, not a parse-time count.
    /// </remarks>
    public int ColumnReferencesParsed;

    /// <summary>
    /// When non-null, every <see cref="Expressions.WindowExpression"/>
    /// constructor registers itself here. Scoped by Selection.Parse around
    /// projection parsing — the executor needs the list to detect the
    /// windowed-projection branch (buffer + partition + sort + bind) and
    /// to know which expressions to bind row-number values into per row.
    /// </summary>
    /// <summary>
    /// When non-null, every <c>NEXT VALUE FOR</c> parsed records its sequence
    /// here. Mirrors <see cref="AggregateCollector"/> / <see cref="WindowCollector"/>:
    /// collecting at construction catches a reference at any nesting depth
    /// without a tree walk. INSERT installs one around its <c>VALUES</c> tuple
    /// parse to enforce Msg 11731.
    /// </summary>
    /// <summary>
    /// When non-null, the parse records the structural facts an indexed view
    /// is judged on into it. Installed only by the validation parse
    /// <c>CREATE INDEX</c> runs over a view's stored body, so every recording
    /// site is a null check on the normal path.
    /// </summary>
    public IndexedViewShape? IndexedViewShapeCollector;

    public List<Schemas.Sequence>? SequenceCollector;

    public List<Expressions.WindowExpression>? WindowCollector;

    /// <summary>
    /// Named-window definitions from a trailing <c>WINDOW w AS (…)</c> clause,
    /// in written order. Populated when the clause is parsed (after HAVING);
    /// consumed to resolve <c>OVER w</c> references. A list rather than a
    /// dictionary because window names compare under the database collation,
    /// which isn't reachable from a field initializer, and a clause never holds
    /// more than a handful of entries. Query-block scoped in practice: resolved
    /// and cleared at the block's projection build. (A WINDOW clause nested in
    /// a subquery of the same statement is a known limitation of the shared
    /// context list.)
    /// </summary>
    public readonly List<(string Name, Expressions.WindowExpression.WindowBody Body)> NamedWindowDefinitions = [];

    /// <summary>
    /// <c>OVER w</c> / <c>OVER (w …)</c> references awaiting resolution against
    /// <see cref="NamedWindowDefinitions"/> — the definition parses after the
    /// projection that references it, so the window is registered carrying only
    /// the reference's own refining elements and patched once the WINDOW clause
    /// is read.
    /// </summary>
    public readonly List<(Expressions.WindowExpression Window, Expressions.WindowExpression.WindowBody Reference)> PendingNamedWindows = [];

    /// <summary>
    /// When false, registering a <see cref="Expressions.WindowExpression"/>
    /// raises Msg 4108 (`"Windowed functions can only appear in the SELECT
    /// or ORDER BY clauses."`). Default true; the Selection parser flips it
    /// false around the WHERE / GROUP BY / HAVING / ON / JOIN-predicate
    /// parses where SQL Server rejects windowed functions.
    /// </summary>
    public bool AllowsWindowExpressions = true;

    /// <summary>
    /// True when expression parsing is inside a clause where SQL Server
    /// rejects <c>NEXT VALUE FOR</c> (probe-confirmed: WHERE / GROUP BY /
    /// HAVING / ORDER BY / TOP / OVER / OUTPUT / ON all raise Msg 11720).
    /// Set by the Selection parser around the affected clauses and consumed
    /// by the <c>NEXT VALUE FOR</c> expression constructor; outside those
    /// scopes (projection / DEFAULT / INSERT VALUES / SET / etc.) the flag
    /// stays false and <c>NEXT VALUE FOR</c> is legal.
    /// </summary>
    public bool RejectNextValueFor;

    /// <summary>
    /// Constructs seen while parsing one branch of a WITH body, recorded
    /// rather than rejected on sight: a branch only becomes the <i>recursive
    /// member</i> — where SQL Server forbids them — once its parse turns up a
    /// self-reference, which can come after the construct itself. The WITH
    /// parser resets this per branch and raises afterwards.
    /// </summary>
    /// <remarks>
    /// Real applies the restriction to the recursive member's whole text, so
    /// a DISTINCT or aggregate inside a nested subquery counts too
    /// (probe-confirmed 2026-07-31) — which is why these are set at the parse
    /// sites rather than read off the branch's own plan.
    /// </remarks>
    public RecursiveMemberConstructs RecursiveBranchConstructs;

    /// <summary>
    /// When true, <see cref="Expression.Parse(ParserContext)"/>'s postfix
    /// loop treats a bare <c>:</c> (not followed by a second <c>:</c>) as
    /// end-of-expression rather than a syntax error. Lets the
    /// <c>JSON_OBJECT(key : value, ...)</c> grammar parse a key expression
    /// that stops at the <c>:</c> separator. Callers save the prior value
    /// and restore in a <c>finally</c> so nested key parses (e.g. a
    /// JSON_OBJECT used as a value inside another JSON_OBJECT's key) don't
    /// leak the flag outside their immediate scope. The <c>::</c>
    /// type-prefix postfix (<c>hierarchyid::Parse(...)</c> etc.) still
    /// resolves normally — only single-colon shapes are affected.
    /// </summary>
    public bool StopExpressionAtBareColon;

    /// <summary>
    /// True while the source <c>SELECT</c> of an <c>INSERT … SELECT</c> is
    /// being parsed, which is where real refuses a <c>FOR XML</c> (Msg 6819) /
    /// <c>FOR JSON</c> (Msg 13602) clause. The INSERT parser sets and restores
    /// it around that one parse; the trailing-clause parsers read it only at
    /// nesting depth 0, so a derived table or subquery inside the source SELECT
    /// keeps its own clause.
    /// </summary>
    public bool InInsertSourceSelect;

    /// <summary>
    /// The output slot whose collation the expression being bound has to
    /// settle on its own — the clause name and 1-based ordinal real names in
    /// Msg 451's <c>occurring in &lt;clause&gt; statement column &lt;n&gt;</c>
    /// tail. Set by the plan build around each select-list / GROUP BY /
    /// ORDER BY term and cleared once the clause is bound.
    /// <para>Null means nothing demands a definite collation at this point:
    /// an assignment target (INSERT … SELECT, SELECT @v = …, UPDATE SET)
    /// supplies one, and real settles the conflict against it silently rather
    /// than raising.</para>
    /// </summary>
    public (string Clause, int Ordinal)? CollationOutputSlot;

    /// <summary>
    /// Parse-time chain of outer-scope column-type resolvers, used to plan
    /// the output schema of a correlated subquery whose projection references
    /// an enclosing SELECT's columns. Set by <see cref="Selection"/>'s
    /// FROM-source dispatch around the WHERE / GROUP BY / HAVING parse so
    /// any nested EXISTS / IN(SELECT) parser sees the chained resolver and
    /// passes it down. Each level captures the prior value so the chain
    /// recurses naturally; null means the top-level scope.
    /// </summary>
    public Func<MultiPartName, SqlType>? OuterTypeResolver;

    /// <summary>
    /// Common-table-expression bindings registered by a <c>WITH</c> prefix
    /// that scope to the immediately-following statement. Populated by
    /// <c>Simulation.ParseCteBindings</c> before the SELECT / INSERT /
    /// UPDATE / DELETE / MERGE dispatch and cleared at the top of the next
    /// statement iteration. Consulted by <c>Selection.ParseSingleFromSource</c>
    /// before falling through to <see cref="Database.Schemas"/>; matching
    /// names build a deferred-plan <see cref="FromSource"/> (re-runs per
    /// reference, parallel to derived tables in FROM). Null when no WITH
    /// prefix is in scope.
    /// </summary>
    public Dictionary<string, CteBinding>? CteBindings;

    /// <summary>
    /// Accumulates the real tables / views / TVFs a query reads, so the
    /// outermost <see cref="Selection.Parse"/> can attach them to the built
    /// <see cref="Selection.ReferencedSecurables"/> for the execution-time
    /// SELECT check. Non-null only while a top-level query expression is being
    /// parsed (nested subqueries / derived tables append to the same list, so
    /// the top-level plan aggregates every read); module bodies parse with
    /// their own <see cref="ParserContext"/> and so never leak into a
    /// caller's list.
    /// </summary>
    public List<ReferencedSecurable>? SecurableSink;

    /// <summary>
    /// Accumulates, per table / view <c>object_id</c>, the set of 1-based column
    /// ordinals a query reads (select list / WHERE / JOIN ON / GROUP BY /
    /// HAVING / ORDER BY), so the outermost <see cref="Selection.Parse"/> can
    /// attach them to <see cref="Selection.ReadColumnsByObject"/> for the
    /// execution-time column-level SELECT check. Principal-independent, so it
    /// rides the cached plan; created alongside <see cref="SecurableSink"/> and
    /// null outside a top-level query expression (module bodies never leak into
    /// a caller's map). An empty column set means the object is read without
    /// naming a specific column (<c>COUNT(*)</c> / <c>SELECT 1</c>), which real
    /// SQL Server checks as requiring SELECT on <em>every</em> column.
    /// </summary>
    public Dictionary<int, ColumnReadTarget>? ReadColumnSink;

    /// <summary>
    /// The <c>GROUP BY</c> item-binding error (Msg 144 / Msg 164) the current
    /// query expression owes, held until its whole statement has parsed. Real
    /// parses a batch before binding any of it, so a syntax error anywhere
    /// past the clause outranks the clause's own binding error:
    /// <c>GROUP BY 'a' 'b'</c> is Msg 102 at <c>'b'</c>, not Msg 164
    /// (probe-confirmed, both messages). The first offending item wins, which
    /// is the order an immediate throw produced.
    /// </summary>
    public SimulatedSqlException? PendingGroupByBindError;

    public Simulation Simulation => Command.simulation;

    /// <summary>
    /// The connection backing <see cref="Command"/>. Always a
    /// <see cref="SimulatedDbConnection"/>: <see cref="SimulatedDbCommand"/>'s
    /// constructor takes one and the setter rejects re-assignment, so once
    /// the command exists this cast is never wrong and never null. Used by
    /// transaction-related parsers and <see cref="Expressions.TranCountExpression"/>
    /// to reach the connection's <see cref="SimulatedDbConnection.CurrentTransaction"/>.
    /// </summary>
    public SimulatedDbConnection Connection => Command.Connection!;

    /// <summary>
    /// The database this batch is executing against. Threads through
    /// <see cref="SimulatedDbConnection.CurrentDatabase"/>; once
    /// <c>USE &lt;db&gt;</c> support lands, switching mid-batch flips this
    /// for subsequent statements without parsers having to thread a separate
    /// pointer.
    /// </summary>
    public Database CurrentDatabase => Connection.CurrentDatabase;

    /// <summary>
    /// Snapshots the current tokenizer position and current token so a
    /// caller can probe the upcoming token via <see cref="MoveNext"/> and
    /// then restore to this point if the lookahead doesn't match. The
    /// tokenizer is index-driven (re-running <see cref="MoveNext"/> from
    /// the saved index produces the same token sequence), so a checkpoint
    /// + restore round-trip is byte-stable.
    /// </summary>
    public (int Index, Token? Token) SaveCheckpoint() => (this.index, this.Token);

    /// <summary>
    /// Raw source text of the command from <paramref name="startIndex"/> up to the
    /// current (lookahead) <see cref="Token"/>, trailing whitespace trimmed —
    /// capturing an expression's original syntax for catalog <c>definition</c>
    /// columns (CHECK / DEFAULT). Pass the <see cref="Token.StartIndex"/> of the
    /// expression's first token, snapshotted before parsing; call once the
    /// expression parse has positioned <see cref="Token"/> on the following token
    /// (or run off the end, where the slice extends to the command's end).
    /// </summary>
    public string SourceTextFrom(int startIndex) =>
        this.commandText[startIndex..(this.Token?.StartIndex ?? this.commandText.Length)].TrimEnd();

    /// <summary>
    /// Restores a checkpoint captured by <see cref="SaveCheckpoint"/>.
    /// </summary>
    public void RestoreCheckpoint((int Index, Token? Token) checkpoint)
    {
        this.index = checkpoint.Index;
        this.Token = checkpoint.Token;
    }

    /// <summary>
    /// Advances <see cref="Token"/> to the next token, if one exists.
    /// </summary>
    public void MoveNextOptional()
    {
        _ = MoveNext();
    }

    /// <summary>
    /// Returns the next token in the enumeration, or null.
    /// </summary>
    /// <returns>The next token if the enumerator was advanced, otherwise null.</returns>
    public Token? GetNextOptional()
    {
        return MoveNext() ? this.Token : null;
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead.
    /// </summary>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public Token GetNextRequired()
    {
        var previous = this.Token;
        return MoveNext() ? this.Token : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead or the token is the wrong type.
    /// </summary>
    /// <typeparam name="T">The expected type of the new token.</typeparam>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public T GetNextRequired<T>()
        where T : Token
    {
        var previous = this.Token;

        return MoveNext() && this.Token is T current ? current : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Advances <see cref="Token"/> to the next token in the enumeration, throwing an exception if the end was reached instead.
    /// The <see cref="ParserContext"/> used for this call is returned.
    /// </summary>
    /// <returns>This instance.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public ParserContext MoveNextRequiredReturnSelf()
    {
        this.MoveNextRequired();
        return this;
    }

    /// <summary>
    /// Advances <see cref="Token"/> to the next token in the enumeration, throwing an exception if the end was reached instead.
    /// </summary>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public void MoveNextRequired()
    {
        var previous = this.Token;
        if (!MoveNext())
            throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Advances <see cref="Token"/> to the next token, throwing an exception
    /// if the end was reached or if the new token isn't of type
    /// <typeparamref name="T"/>. Use when the caller needs the type assertion
    /// but not the token value — pairs with <see cref="GetNextRequired{T}"/>
    /// the same way <see cref="MoveNextRequired"/> pairs with
    /// <see cref="GetNextRequired"/>.
    /// </summary>
    /// <typeparam name="T">The expected type of the new token.</typeparam>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public void MoveNextRequired<T>()
        where T : Token
    {
        var previous = this.Token;
        if (!MoveNext() || this.Token is not T)
            throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Updates <see cref="Token"/> with the next usable token in <see cref="commandText"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Whitespace"/> and <see cref="Comment"/> tokens are skipped.
    /// <see cref="index"/> is updated to the position of the next token.
    /// </remarks>
    /// <returns>True if another token was found, otherwise false.</returns>
    [MemberNotNullWhen(true, nameof(Token))]
    public bool MoveNext()
    {
        while (Tokenizer.NextToken(commandText, ref index, this.CurrentDatabase.Collation, this.QuotedIdentifiers, this.CurrentDatabase.CompatibilityLevel) is Token token)
        {
            if (token is Whitespace or Comment)
                continue;

#if DEBUG
            tokens.Add(token);
#endif
            this.Token = token;
            return true;
        }

        this.Token = null;
        return false;
    }

#if DEBUG
    /// <summary>
    /// Contains all the non-whitespace tokens that have been read so far.
    /// </summary>
    private readonly List<Token> tokens = [];

    /// <summary>
    /// Returns a string representation of the tokenized command.
    /// The <see cref="Token"/> token is wrapped by '»' and '«'.
    /// </summary>
    /// <returns>The string representation.</returns>
    public override string ToString()
    {
        var command = this.commandText;
        Span<char> result = stackalloc char[command.Length + 2];
        if (this.Token is { } token)
        {
            token.Highlight(result);
        }
        else if (index >= command.Length)
        {
            command.CopyTo(result);
            result[^2] = '»';
            result[^1] = '«';
        }
        else
        {
            // Pre-MoveNext state: cursor at the start.
            result[0] = '»';
            result[1] = '«';
            command.CopyTo(result[2..]);
        }

        return new string(result);
    }
#endif
}
