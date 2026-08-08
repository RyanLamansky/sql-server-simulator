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
    /// returned token (see <see cref="Tokenizer"/>'s index contract). Kept
    /// accurate while a token memo is being replayed too, so abandoning the
    /// memo mid-parse resumes live tokenization at the right character.
    /// </summary>
    private int index;

    /// <summary>
    /// The <see cref="TokenMemo"/> entry being replayed, or
    /// <see langword="null"/> when this parse is tokenizing live. Its tokens
    /// are shared with every other execution of the same text and are read,
    /// never written.
    /// </summary>
    private Token[]? memoTokens;

    /// <summary>Read position within <see cref="memoTokens"/>.</summary>
    private int memoPosition;

    /// <summary>
    /// Tokens gathered for publication when this parse is the one that
    /// populates the memo. Null when a memo is already being replayed, when
    /// the memo is full, or once the collection has been abandoned.
    /// </summary>
    private List<Token>? memoCollector;

    /// <summary>
    /// The tokenization inputs this context bound to. Re-checked on every
    /// <see cref="MoveNext"/>: a mid-batch <c>SET QUOTED_IDENTIFIER</c> or
    /// <c>USE</c> changes what the remaining characters tokenize to, and both
    /// abandon the memo rather than serve tokens from the wrong inputs.
    /// </summary>
    private TokenMemoKey memoKey;

    /// <summary>Whether <see cref="BindTokenMemo"/> has run for this context.</summary>
    private bool memoBound;

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
    /// The last token <see cref="MoveNext"/> produced before it ran out of
    /// input, which is what an end-of-batch syntax error names. Real reports
    /// the last token it consumed rather than an empty slot — probed against
    /// SQL Server 2025 (2026-08-05) across the whole family:
    /// <c>SELECT abs(-1</c> → <c>near '1'</c>, <c>SELECT 1 FROM</c> →
    /// <c>near 'FROM'</c>, <c>IF 'abc'</c> → Msg 4145 <c>near 'abc'</c>. Only
    /// read once <see cref="Token"/> is null; a checkpoint restore leaves it
    /// pointing past the restore, which no live parse can observe.
    /// </summary>
    public Token? LastToken;

    /// <summary>
    /// How many parenthesized <em>boolean</em> groups the predicate parser is
    /// currently inside. Read only by the Msg 4145 factory: real settles the
    /// non-boolean diagnostic against the token following the whole
    /// parenthesized expression, so <c>IF ((1)) PRINT 'x'</c> names
    /// <c>'PRINT'</c> where the simulator's grammar (which consumes the parens
    /// on the way in) is sitting on the innermost <c>)</c>. Probed 2026-08-05
    /// across the family — <c>SELECT 1 WHERE (1)</c> still names <c>')'</c>,
    /// because nothing follows it.
    /// </summary>
    public int BooleanGroupDepth;

    /// <summary>
    /// Whether the query specification that parsed most recently was a bare
    /// <c>SELECT &lt;expression list&gt;</c> — no FROM, WHERE, ORDER BY, TOP,
    /// DISTINCT, INTO or subquery. Read once the outermost query expression is
    /// complete, to settle Msg 422: real refuses a <c>WITH</c> prefix on
    /// exactly that shape and accepts every other one, a trailing
    /// <c>WHERE 1 = 1</c> / <c>ORDER BY 1</c> / <c>UNION</c> / <c>FOR JSON</c>
    /// / <c>OPTION (…)</c> included (probed 2026-08-05).
    /// </summary>
    public bool LastQuerySpecIsBareProjection;

    /// <summary>
    /// Whether the <c>WITH</c> prefix the dispatch loop just parsed leads a
    /// <c>SELECT</c> statement rather than an <c>INSERT</c> / <c>UPDATE</c> /
    /// <c>DELETE</c> / <c>MERGE</c>. Msg 422 asks only about a SELECT: real
    /// accepts an unused prefix on every DML form, including the one whose
    /// source is the bare projection it refuses on its own
    /// (<c>WITH c AS (…) INSERT INTO t SELECT 1</c>, probed 2026-08-05). A
    /// stored body that parses outside the dispatch loop — a view, an inline
    /// TVF, a cursor declaration — never sets it, which is what keeps those
    /// accepting the shape as real does.
    /// </summary>
    public bool CtePrefixLeadsSelectStatement;

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
    /// <see cref="Expression.VisitColumnReferences(Action{MultiPartName})"/>, so a tree walk silently
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
    /// Armed while an operand that admits only scalar expressions is parsed —
    /// <c>PRINT</c>'s is the one such site. It has no column scope and no
    /// rowset scope, so a name reports <b>Msg 128</b> and a subquery reports
    /// <b>Msg 1046</b>, whichever the reading meets first.
    /// </summary>
    public bool ScalarOnlyOperand;

    /// <summary>
    /// The first column reference met while <see cref="ScalarOnlyOperand"/> is
    /// armed. The reference is recorded rather than its name, since the dotted
    /// parts are appended after construction — the whole multi-part name only
    /// exists once the postfix loop is done with it, and Msg 128 names it as
    /// written. Left-to-right precedence falls out of the recording order: a
    /// subquery met later reports this name instead of its own error.
    /// </summary>
    public Expressions.Reference? ScalarOnlyColumnReference;

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
    /// Which of SQL Server's <c>NEXT VALUE FOR</c> refusals the expression
    /// currently being parsed sits under, or <see cref="NextValueForScope.Allowed"/>
    /// in the positions that stay legal (a bare projection, a <c>VALUES</c>
    /// tuple, a column <c>DEFAULT</c>, a stored procedure's own statements).
    /// Each rejecting construct's parse sets it for its own duration and the
    /// <c>NEXT VALUE FOR</c> constructor consumes it, so the whole batch is
    /// refused at parse — which is what keeps the sequence from advancing.
    /// </summary>
    public NextValueForScope NextValueForRejection;

    /// <summary>
    /// Applies <paramref name="scope"/> to <see cref="NextValueForRejection"/>
    /// as a <em>floor</em> — it takes effect only when nothing stricter is
    /// already in force, since <see cref="NextValueForScope"/> declares its
    /// arms in real's own precedence order. Returns the previous value for the
    /// caller to restore in a <c>finally</c>.
    /// </summary>
    public NextValueForScope EnterNextValueForScope(NextValueForScope scope)
    {
        var saved = this.NextValueForRejection;
        if (saved == NextValueForScope.Allowed || scope < saved)
            this.NextValueForRejection = scope;
        return saved;
    }

    /// <summary>
    /// How many <c>NEXT VALUE FOR</c> references have been parsed in a
    /// position real accepts. Two of real's refusals are properties of the
    /// whole statement rather than of the reference's own position — Msg
    /// 11721 for a set operator and Msg 11723 for an <c>ORDER BY</c> — and
    /// neither is known until after the select list has been read, so each is
    /// settled by comparing a counter against a snapshot taken where the
    /// statement began.
    /// </summary>
    public int SequenceDrawsParsed;

    /// <summary>
    /// The subset of <see cref="SequenceDrawsParsed"/> whose reference named
    /// no <c>OVER</c> clause of its own. An <c>OVER</c> exempts a reference
    /// from the <c>ORDER BY</c> refusal (Msg 11723) and from that one alone —
    /// probe-confirmed that it exempts from none of the others, the set
    /// operator's included — so the two checks read different counters.
    /// </summary>
    public int UnwindowedSequenceDrawsParsed;

    /// <summary>
    /// True while an <c>UPDATE</c> / <c>DELETE</c>'s own <c>FROM</c> clause is
    /// parsed, where real leaves a derived table's <c>NEXT VALUE FOR</c> legal
    /// (probe-confirmed) although every other derived table refuses it.
    /// </summary>
    public bool AllowNextValueForInFromClause;

    /// <summary>
    /// Collects the column references parsed while a <b>non-APPLY</b> FROM
    /// source's own arguments are read — a table-valued function's arguments,
    /// <c>STRING_SPLIT</c> / <c>OPENJSON</c>'s, a <c>VALUES</c> constructor's
    /// cells. SQL Server binds those in a scope that excludes the FROM's own
    /// sources (only <c>APPLY</c> grants laterality), so a reference landing
    /// on a sibling is Msg 4104 there; the collected list is what the check
    /// runs over once every source is parsed and the sibling set is known.
    /// Null everywhere else, including inside any nested query body — a
    /// <see cref="Selection"/> parse suspends it, so a derived table's own
    /// references never leak into the enclosing source's list.
    /// </summary>
    public List<Expressions.Reference>? FromSourceColumnSink;

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
    /// The nesting depth at which a parenthesized <c>INSERT</c> source's own
    /// query is being parsed, or null outside one. That query may not carry an
    /// <c>ORDER BY</c> — real refuses it there even with the <c>TOP</c> that
    /// would license one in a derived table, as <strong>Msg 156</strong>.
    /// </summary>
    /// <remarks>
    /// Recorded as a depth rather than a bool because the restriction is the
    /// source query's alone: a derived table or subquery <em>inside</em> it
    /// parses deeper and keeps the ordinary rules, so
    /// <c>INSERT … (SELECT x FROM (SELECT TOP 1 v FROM u ORDER BY v) d)</c>
    /// stays legal.
    /// </remarks>
    public uint? ParenthesizedInsertSourceDepth;

    /// <summary>
    /// Set for a statement whose parser leaves the cursor at its first
    /// <em>un</em>-consumed token, so anything there that isn't a statement
    /// boundary is unconsumed input rather than the parse's own tail.
    /// </summary>
    /// <remarks>
    /// The dispatch loop can't tell the two apart from the cursor alone: most
    /// parsers stop on the last token they consumed and need one advance,
    /// while these stop past it. Advancing unconditionally is what let a stray
    /// token vanish — <c>DECLARE @x int = 1 zzz</c> ran clean where real
    /// raises Msg 102. Marking is opt-in per statement kind because it's a
    /// claim about that parser's cursor discipline, verified against real
    /// rather than assumed.
    /// </remarks>
    private bool statementOwnsItsTrailingToken;

    /// <summary>
    /// Declares that this statement's parser consumed everything it owns, so
    /// a non-boundary token left behind is Msg 102.
    /// </summary>
    public void RejectTrailingToken() => this.statementOwnsItsTrailingToken = true;

    /// <summary>
    /// Reads and clears the flag. Clearing keeps it per-statement — one
    /// statement's discipline says nothing about the next one's.
    /// </summary>
    public bool ConsumeRejectTrailingToken()
    {
        var value = this.statementOwnsItsTrailingToken;
        this.statementOwnsItsTrailingToken = false;
        return value;
    }

    /// <summary>
    /// Set on the <c>SELECT</c> that sits immediately inside an <c>EXISTS</c>,
    /// whose projection real never materializes — an unresolved collation in
    /// that select list settles into nothing rather than reporting Msg 451
    /// (probe-confirmed: <c>EXISTS (SELECT concat(a, b) …)</c> returns rows
    /// where the same projection at statement level raises).
    /// <para>Claimed and cleared by the single-SELECT parse that consumes it,
    /// so a derived table or subquery nested inside the <c>EXISTS</c> body
    /// still names its own output collation.</para>
    /// </summary>
    public bool ProjectionDiscarded;

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
    /// The FROM sources of the query level being parsed. Installed by
    /// <see cref="Selection"/> as soon as the FROM clause is parsed — before the
    /// select list and before WHERE — and restored on the way out, so each level
    /// offers its own scope. Null where no query scope exists (a CHECK
    /// constraint, a computed column).
    /// </summary>
    /// <remarks>
    /// Two parse-time decisions read it: a <c>CONTAINS</c> / <c>FREETEXT</c>
    /// predicate binds its column specification here (real reports Msg 1046
    /// where there is no scope), and a dotted name whose leaf is a spatial
    /// member name asks whether its qualifier is a spatial <i>column</i>, which
    /// is what tells <c>Location.Lat</c>'s property read apart from an
    /// <c>alias.column</c> reference.
    /// </remarks>
    public FromSource[]? ScopeSources;

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
    /// The namespace bindings a <c>WITH XMLNAMESPACES (…)</c> prefix declares,
    /// scoped — like <see cref="CteBindings"/> — to the immediately-following
    /// statement and cleared at the top of the next iteration. Read by every
    /// <c>FOR XML</c> clause the statement contains, nested subqueries
    /// included, since real re-declares the bindings on each serialized
    /// fragment's outermost element. Null when no such prefix is in scope.
    /// </summary>
    public ForXmlNamespaces? XmlNamespaces;

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
    /// A saved parse position: the tokenizer's character index, the current
    /// token, and the ordinal of that token within the batch's token sequence.
    /// All three restore together, and the parser moves the cursor both
    /// backwards and forwards through saved checkpoints — a lookahead that
    /// scans ahead, rewinds to re-parse from an earlier point, then jumps back
    /// to where the scan stopped is the <c>FROM</c>-clause probe's shape.
    /// </summary>
    public readonly struct Checkpoint(int index, Token? token, int memoPosition)
    {
        public readonly int Index = index;
        public readonly Token? Token = token;
        public readonly int MemoPosition = memoPosition;
    }

    /// <summary>
    /// Snapshots the current tokenizer position and current token so a
    /// caller can probe the upcoming token via <see cref="MoveNext"/> and
    /// then restore to this point if the lookahead doesn't match. The
    /// tokenizer is index-driven (re-running <see cref="MoveNext"/> from
    /// the saved index produces the same token sequence), so a checkpoint
    /// + restore round-trip is byte-stable.
    /// </summary>
    public Checkpoint SaveCheckpoint() => new(this.index, this.Token, this.memoPosition);

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
    /// Whether this batch's raw text carries anything a <c>label:</c> or a
    /// <c>GOTO</c> could be spelled with — a necessary condition for both, so
    /// a false reading lets the label pre-scan skip its token walk outright.
    /// The two vectorized text searches are far cheaper than the walk they
    /// replace, and almost every batch fails them.
    /// </summary>
    public bool MightCarryLabelsOrGoto =>
        this.commandText.Contains(':', StringComparison.Ordinal)
        || this.commandText.Contains("goto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Restores a checkpoint captured by <see cref="SaveCheckpoint"/>.
    /// </summary>
    public void RestoreCheckpoint(Checkpoint checkpoint)
    {
        this.index = checkpoint.Index;
        this.Token = checkpoint.Token;
        this.memoPosition = checkpoint.MemoPosition;
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
        if (!this.memoBound)
        {
            BindTokenMemo();
        }
        else if ((this.memoTokens is not null || this.memoCollector is not null) && !TokenMemoInputsUnchanged())
        {
            // A mid-batch SET QUOTED_IDENTIFIER or USE changed what the
            // remaining characters tokenize to. Everything consumed so far was
            // read under the old inputs and stays valid — `index` is one past
            // it — so live tokenization simply resumes from here, and nothing
            // is published (the sequence would be a splice of two settings).
            this.memoTokens = null;
            this.memoCollector = null;
        }

        if (this.memoTokens is { } memo)
        {
            if (this.memoPosition < memo.Length)
            {
                var memoized = memo[this.memoPosition++];
                this.index = memoized.EndIndex;
#if DEBUG
                tokens.Add(memoized);
#endif
                this.Token = memoized;
                return true;
            }

            this.index = commandText.Length;
            this.LastToken = this.Token;
            this.Token = null;
            return false;
        }

        try
        {
            while (Tokenizer.NextToken(commandText, ref index, this.CurrentDatabase.Collation, this.QuotedIdentifiers, this.CurrentDatabase.CompatibilityLevel) is Token token)
            {
                if (token is Whitespace or Comment)
                    continue;

#if DEBUG
                tokens.Add(token);
#endif
                CollectToken(token);
                this.memoPosition++;
                this.Token = token;
                return true;
            }
        }
        catch (SimulatedSqlException)
        {
            // The tokenizer refused a character (Msg 102 / 103 / 105 / 113).
            // It leaves `index` past the text it was mid-way through, so a
            // later scan — the dispatch loop's error recovery — resumes beyond
            // the refused span and can still reach end-of-text. Collecting
            // through that would publish a sequence with the refused span
            // missing, and the next execution would parse the hole instead of
            // raising: the probe that found this reported Msg 105 once and
            // Msg 102 forever after. A text the tokenizer won't read is a text
            // with no sequence to share.
            this.memoCollector = null;
            throw;
        }

        // End of text reached with no tokenization error: the collected
        // sequence is complete and safe to share. Publishing here — and only
        // here — is what keeps a text whose tokenizer raises mid-way out of
        // the memo, so its error fires at the same character every time.
        // The count check is defensive: a sequence with an unwritten slot
        // would be a hole, and the parse that produced it is not one worth
        // trusting to serve every later execution.
        if (this.memoCollector is { } collected && collected.Count == this.memoPosition)
        {
            if (!RewritesTokenizationInputs(collected))
                this.Simulation.TokenMemo.Publish(this.memoKey, [.. collected]);
            this.memoCollector = null;
        }

        this.LastToken = this.Token;
        this.Token = null;
        return false;
    }

    /// <summary>
    /// Whether a completed token sequence names a construct that can change
    /// the tokenizer's own inputs partway through the same text — a
    /// <c>SET QUOTED_IDENTIFIER</c> (or the <c>ANSI_DEFAULTS</c> bundle that
    /// carries it), a <c>USE</c> switching the database whose collation and
    /// compatibility level the tokenizer reads, or an <c>ALTER</c> that could
    /// re-collate or re-level the current one. No single token sequence is
    /// correct for such a text, because its two halves tokenize under
    /// different settings.
    /// <para>
    /// The live parse handles this by abandoning the memo the moment the
    /// inputs move; this is the other half, and it exists because tokenizing
    /// runs ahead of dispatch. A lookahead that scans to end-of-text — the
    /// <c>FROM</c>-clause probe does, over a batch with no separators —
    /// completes the sequence <em>before</em> the statement that flips the
    /// setting has run, so abandonment alone would leave a wrong sequence
    /// already published. Judging the finished sequence needs no ordering
    /// assumption at all.
    /// </para>
    /// </summary>
    private static bool RewritesTokenizationInputs(List<Token> collected)
    {
        foreach (var token in collected)
        {
            switch (token)
            {
                case ReservedKeyword { Keyword: Keyword.Use or Keyword.Alter }:
                    return true;
                case Name name when name.Span.Equals("QUOTED_IDENTIFIER", StringComparison.OrdinalIgnoreCase)
                    || name.Span.Equals("ANSI_DEFAULTS", StringComparison.OrdinalIgnoreCase):
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records a live-tokenized token at its ordinal position in the sequence
    /// being collected. Addressed by <see cref="memoPosition"/> rather than
    /// appended because the parser re-reads: a checkpoint restore rewinds the
    /// cursor and the tokens after it are read again. Re-reading the same
    /// character position under unchanged inputs produces an equal token, so
    /// overwriting the slot is a no-op in content; what matters is that the
    /// slot's <em>ordinal</em> is the one the token belongs at, which append
    /// would get wrong the moment a restore jumped the cursor forward again.
    /// </summary>
    private void CollectToken(Token token)
    {
        if (this.memoCollector is not { } collector)
            return;
        if (this.memoPosition < collector.Count)
            collector[this.memoPosition] = token;
        else if (this.memoPosition == collector.Count)
            collector.Add(token);
        else
            this.memoCollector = null; // Unreachable: a slot was never written.
    }

    /// <summary>
    /// Binds this parse to the simulation's token memo on the first
    /// <see cref="MoveNext"/>: either replaying a stored sequence for this
    /// text, or collecting one for the next execution. Deferred to first use
    /// rather than done in a field initializer so a context built and never
    /// walked costs nothing.
    /// </summary>
    private void BindTokenMemo()
    {
        this.memoBound = true;
        var database = this.CurrentDatabase;
        this.memoKey = new TokenMemoKey(commandText, database.Collation, database.CompatibilityLevel, this.QuotedIdentifiers);
        var memo = this.Simulation.TokenMemo;
        if (memo.TryGet(this.memoKey) is { } stored)
            this.memoTokens = stored;
        else if (memo.HasCapacity)
            this.memoCollector = [];
    }

    /// <summary>
    /// Whether the tokenization inputs still match the ones
    /// <see cref="BindTokenMemo"/> captured. <c>QUOTED_IDENTIFIER</c> flips
    /// mid-batch at the textual position of its <c>SET</c>, and <c>USE</c>
    /// swaps the database whose collation tags string literals and whose
    /// compatibility level decides the reserved words — both change what the
    /// characters after them tokenize to.
    /// </summary>
    private bool TokenMemoInputsUnchanged()
    {
        var database = this.CurrentDatabase;
        return this.QuotedIdentifiers == this.memoKey.QuotedIdentifiers
            && ReferenceEquals(database.Collation, this.memoKey.Collation)
            && database.CompatibilityLevel == this.memoKey.CompatibilityLevel;
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

/// <summary>
/// The construct a <c>NEXT VALUE FOR</c> reference sits inside, and with it
/// which of SQL Server's refusals applies. Probed against SQL Server 2025
/// (2026-08-05); each arm carries the message real raises, and every one of
/// them is a <em>parse</em>-time refusal of the whole batch, so the sequence
/// never advances and a <c>TRY</c> in the same batch never sees it.
/// <para>
/// <b>Declaration order is the precedence order</b>, lowest ordinal winning:
/// a reference that sits under two restrictions at once reports the earlier
/// arm. Every neighbouring pair was probed directly — a <c>DISTINCT</c>
/// statement whose <c>WHERE</c> holds the reference is Msg 11721 not 11720, a
/// <c>TOP</c> query whose <c>WHERE</c> holds it is 11720 not 11739, an
/// aggregate argument inside a <c>CASE</c> is 11725 not 11741, and so on.
/// <see cref="ParserContext.EnterNextValueForScope"/> is what applies the
/// order, so a construct nested inside a stricter one never relaxes it.
/// </para>
/// </summary>
internal enum NextValueForScope
{
    /// <summary>A position real accepts: a bare projection, a <c>VALUES</c> tuple, a column <c>DEFAULT</c>, a stored procedure's own statement.</summary>
    Allowed,

    /// <summary>A nested query or stored expression — a derived table, a CTE, a subquery, an <c>APPLY</c> body, a view / function body, a computed column, a <c>CHECK</c> constraint, a table type's default. Msg 11719.</summary>
    Nested,

    /// <summary>An aggregate's argument. Msg 11725.</summary>
    Aggregate,

    /// <summary>A statement that dedupes or combines rowsets — <c>DISTINCT</c>, <c>UNION</c>, <c>UNION ALL</c>, <c>EXCEPT</c>, <c>INTERSECT</c>. Msg 11721.</summary>
    Deduplicating,

    /// <summary>A statement carrying an <c>ORDER BY</c>, where the reference names no <c>OVER</c> of its own. Msg 11723.</summary>
    OrderedStatement,

    /// <summary>A clause of the query — <c>TOP</c> / <c>OVER</c> / <c>OUTPUT</c> / <c>ON</c> / <c>WHERE</c> / <c>GROUP BY</c> / <c>HAVING</c> / <c>ORDER BY</c>. Msg 11720.</summary>
    Clause,

    /// <summary>A statement carrying a <c>TOP</c> or an <c>OFFSET</c>. Msg 11739.</summary>
    RowLimited,

    /// <summary>An arm of <c>CASE</c> / <c>COALESCE</c> / <c>IIF</c> / <c>ISNULL</c> / <c>NULLIF</c>. Msg 11741 — which names <c>CHOOSE</c> too, though real accepts that one.</summary>
    Conditional,

    /// <summary>A <c>MERGE</c> action's own expression. Msg 11742 — only a default constraint on the target may draw from a sequence there.</summary>
    MergeAction,

    /// <summary>A statement real declines to define the reference in at all, such as <c>PRINT</c>. Msg 11738.</summary>
    Unsupported,
}
