using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Where in a query block a scalar function call sits, as far as real's
/// scalar UDF inlining tells positions apart.
/// </summary>
internal enum InliningClause : byte
{
    /// <summary>Anywhere not named below: a <c>WHERE</c>, a join, a <c>HAVING</c>, a DML clause.</summary>
    Other,

    /// <summary>The block's select list.</summary>
    SelectList,

    /// <summary>The block's <c>GROUP BY</c>, whose calls are never inlined and which covers the select list's.</summary>
    GroupBy,

    /// <summary>An <c>ORDER BY</c>, whose own calls are never inlined.</summary>
    OrderBy,
}

/// <summary>What the statement being compiled lets its calls inline into.</summary>
internal enum InliningStatement : byte
{
    /// <summary>A statement that is no query (<c>SET</c>, <c>DECLARE</c>, <c>PRINT</c> …): only a subquery in it is.</summary>
    NonQuery,

    /// <summary>A <c>SELECT</c>, <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c>.</summary>
    Query,

    /// <summary>
    /// A statement led by a common table expression, whose calls real never
    /// inlines, or a <c>CREATE</c> / <c>ALTER</c>, which compiles no plan.
    /// </summary>
    Barred,
}

/// <summary>
/// The scalar function calls a compile would inline, gathered while the batch
/// compile walk or an inlining attempt reads its text: real SQL Server inlines
/// an inlineable scalar UDF into the query calling it as the query compiles,
/// and a body that no longer binds — it names an object since dropped — is a
/// non-aborting error attributed to the function, once per call, sent while
/// the batch compiles (probed 2026-09-30 against SQL Server 2025).
/// </summary>
/// <remarks>
/// <para>
/// Which calls real inlines was probed call site by call site: one in a
/// query's select list, <c>WHERE</c>, join, <c>HAVING</c>, <c>TOP</c>, an
/// <c>INSERT</c>'s <c>VALUES</c>, an <c>UPDATE</c>'s <c>SET</c>, a
/// <c>MERGE</c>, a cursor's query, a view's or inline function's body, and a
/// subquery anywhere outside an <c>IF</c> / <c>WHILE</c> condition (a
/// <c>DECLARE</c>'s or <c>SET</c>'s included) is; one in an <c>ORDER BY</c>
/// or <c>GROUP BY</c>, a <c>DECLARE</c> / <c>SET</c> / <c>PRINT</c> /
/// <c>IF</c> operand, a statement led by a common table expression or carrying
/// <c>USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING')</c>, and the select list of
/// a <c>SELECT</c> that only assigns variables and reads no <c>FROM</c> or
/// <c>WHERE</c> is not. A block that is <c>DISTINCT</c> and ordered inlines
/// nothing in its select list, a select-list call its <c>GROUP BY</c> also
/// makes is its grouping column, and an ordered set operation inlines none of
/// its branches' calls.
/// </para>
/// <para>
/// A statement real binds only when it runs — its FROM names an object that
/// doesn't exist yet — compiles nothing here, and a statement whose binder
/// failed before reaching a call never inlines it.
/// </para>
/// </remarks>
internal sealed class InlinedScalarCalls(List<SimulatedSqlException>? errors, bool body)
{
    /// <summary>
    /// The binder errors the compile walk gathers, whose count places each
    /// call's failure among them; null for an inlining attempt.
    /// </summary>
    private readonly List<SimulatedSqlException>? errors = errors;

    /// <summary>
    /// True while a function body is read as real inlines it, where every call
    /// is part of the one expression the body becomes.
    /// </summary>
    public readonly bool Body = body;

    /// <summary>The calls gathered, in binding order.</summary>
    public readonly List<InlinedScalarCall> Calls = [];

    /// <summary>What the statement being read lets its calls inline into.</summary>
    public InliningStatement Statement;

    /// <summary>
    /// The first object an inlining attempt's body names that doesn't exist,
    /// with the line of the reference in the text that created the function.
    /// </summary>
    public (MultiPartName Name, int Line)? MissingObject;

    private int blocks;

    /// <summary>Whether the collector gathers a batch's compile, rather than a function body's or one statement's as it runs.</summary>
    public bool CompilesBatch => this.errors is not null;

    /// <summary>
    /// The statements the batch's compile leaves to compile again as they run;
    /// see <see cref="BatchContext.StatementsCompiledOnRun"/>.
    /// </summary>
    public StatementsCompiledOnRun? CompiledOnRun;

    /// <summary>
    /// Whether a statement compiles again every time it runs with calls to
    /// inline, which keeps the batch's own compile from being reused: real
    /// compiles such a batch afresh each time, sending its failures again.
    /// </summary>
    public bool RecompilesEveryRun;

    /// <summary>
    /// Notes that the statement starting at <paramref name="start"/> compiles
    /// as it runs — every time it runs when <paramref name="everyRun"/>.
    /// </summary>
    public void CompileOnRun(int start, bool everyRun)
    {
        (this.CompiledOnRun ??= new()).Add(start, everyRun);
        this.RecompilesEveryRun |= everyRun;
    }

    /// <summary>
    /// Notes that the compile stopped short at <paramref name="stoppedAt"/> —
    /// a statement it deferred whose end it couldn't find — so every statement
    /// starting past it compiles when it first runs.
    /// </summary>
    public void CompileOnRunFrom(int stoppedAt) => (this.CompiledOnRun ??= new()).From = stoppedAt;

    /// <summary>Whether a call gathered from <paramref name="mark"/> on is kept.</summary>
    public bool KeepsAnyFrom(int mark)
    {
        for (var i = mark; i < this.Calls.Count; i++)
        {
            if (!this.Calls[i].Dropped)
                return true;
        }
        return false;
    }

    /// <summary>A number for a query block about to be parsed, which its calls carry.</summary>
    public int OpenBlock() => ++this.blocks;

    /// <summary>
    /// Records a call to <paramref name="function"/> — a scalar function, or
    /// an inline table-valued function whose body expands in its place — at
    /// the parser's position when real would inline it there.
    /// </summary>
    public static void Note(ParserContext context, UserDefinedFunction function)
    {
        var batch = context.Batch;
        if (batch.InlinedCalls is not { } calls || context.InliningClause == InliningClause.OrderBy)
            return;
        if (!calls.Body
            && (calls.Statement == InliningStatement.Barred
                || context.ConditionDepth > 0
                || (calls.Statement == InliningStatement.NonQuery && context.SecurableSink is null)))
        {
            return;
        }
        if (!Inlines(batch, function))
            return;
        var at = context.Token;
        calls.Calls.Add(new InlinedScalarCall(function, context.InliningBlock, context.InliningClause, calls.errors?.Count ?? 0, batch.BindErrors?.Covers(at) == true ? at!.StartIndex : -1));
    }

    /// <summary>
    /// Whether real inlines <paramref name="function"/> into a query compiled
    /// in <paramref name="batch"/>'s database: an inline table-valued function
    /// always expands, and a scalar one inlines at compatibility level 150 or
    /// more with <c>TSQL_SCALAR_UDF_INLINING</c> on, when
    /// <see cref="ModuleInlining"/> reports its body inlineable (probed
    /// 2026-09-30 against SQL Server 2025: level 140, the configuration off,
    /// <c>WITH INLINE = OFF</c> and a non-inlineable body each send nothing).
    /// </summary>
    private static bool Inlines(BatchContext batch, UserDefinedFunction function) =>
        function is not ScalarFunction scalar
        || (batch.CurrentDatabase is { CompatibilityLevel: >= CompatibilityLevel.Sql150, ScopedConfiguration.TsqlScalarUdfInlining: true }
            && (scalar.BodyInlines ??= ModuleInlining.Evaluate(scalar).InlineType));

    /// <summary>
    /// Records the first object an inlining attempt's body names that doesn't
    /// exist, at <paramref name="line"/> of the body's text.
    /// </summary>
    public static void NoteMissingObject(BatchContext batch, MultiPartName name, int line)
    {
        if (batch.InlinedCalls is { Body: true, MissingObject: null } calls)
            calls.MissingObject = (name, line);
    }

    /// <summary>
    /// Settles the calls gathered from <paramref name="mark"/> on as a
    /// statement is read for its whole binder report: one
    /// <paramref name="report"/> holds an error ahead of in the binder's order
    /// is dropped, real meeting the error first and so never inlining it, and
    /// one in a statement nested in it — an <c>IF</c>'s branch, which reports
    /// for itself — fails after the report (probed 2026-09-30 against SQL
    /// Server 2025).
    /// </summary>
    public void DropBehindErrors(BindErrorReport report, int mark)
    {
        for (var i = mark; i < this.Calls.Count; i++)
        {
            var call = this.Calls[i];
            if (call.Position < 0)
                call.ErrorPosition++;
            else if (report.AnyErrorSortsBefore(call.Position))
                call.Dropped = true;
        }
    }

    /// <summary>Drops every call gathered from <paramref name="mark"/> on.</summary>
    public void DropFrom(int mark)
    {
        for (var i = mark; i < this.Calls.Count; i++)
            this.Calls[i].Dropped = true;
    }

    /// <summary>
    /// Forgets every call gathered from <paramref name="mark"/> on, for a
    /// statement read again for its whole binder report.
    /// </summary>
    public void TruncateTo(int mark)
    {
        if (mark < this.Calls.Count)
            this.Calls.RemoveRange(mark, this.Calls.Count - mark);
    }

    /// <summary>
    /// Settles the calls of the query block numbered <paramref name="block"/>
    /// once it has parsed: a call its <c>GROUP BY</c> makes is never inlined
    /// and makes the select list's call to the same function its grouping
    /// column, and <paramref name="dropsSelectList"/> — a <c>DISTINCT</c>,
    /// ordered block, or one that only assigns variables reading no
    /// <c>FROM</c> or <c>WHERE</c> — drops the select list's.
    /// </summary>
    public void SettleBlock(int block, bool dropsSelectList)
    {
        foreach (var call in this.Calls)
        {
            if (call.Block != block)
                continue;
            if (call.Clause == InliningClause.GroupBy)
            {
                call.Dropped = true;
                foreach (var covered in this.Calls)
                {
                    if (covered.Block == block && covered.Clause == InliningClause.SelectList && ReferenceEquals(covered.Function, call.Function))
                        covered.Dropped = true;
                }
            }
            else if (dropsSelectList && call.Clause == InliningClause.SelectList)
            {
                call.Dropped = true;
            }
        }
    }
}

/// <summary>One call <see cref="InlinedScalarCalls"/> gathered.</summary>
internal sealed class InlinedScalarCall(UserDefinedFunction function, int block, InliningClause clause, int errorPosition, int position)
{
    public readonly UserDefinedFunction Function = function;

    /// <summary>The query block the call sits in, 0 outside any.</summary>
    public readonly int Block = block;

    public readonly InliningClause Clause = clause;

    /// <summary>How many binder errors the compile had gathered when the call bound, which places its failure among them.</summary>
    public int ErrorPosition = errorPosition;

    /// <summary>
    /// Where in the statement's text the call sits, while the statement is
    /// read for its whole binder report; -1 otherwise.
    /// </summary>
    public readonly int Position = position;

    /// <summary>Set once the statement or block the call sits in turns out not to inline it.</summary>
    public bool Dropped;
}

/// <summary>
/// Why real couldn't inline a scalar function: an object its body names that
/// doesn't exist — the Msg 208 real sends attributed to the function, at the
/// line of the reference in the text that created it.
/// </summary>
internal sealed class InliningFailure(MultiPartName name, int line, string procedure)
{
    private readonly MultiPartName name = name;
    private readonly int line = line;
    private readonly string procedure = procedure;

    /// <summary>A fresh error for one call that failed.</summary>
    public SimulatedSqlException Raise()
    {
        var error = SimulatedSqlException.InvalidObjectName(this.name);
        error.ResolveDiagnostics(this.line, 0, this.procedure);
        return error;
    }
}

/// <summary>
/// The statements of a batch that compile as they run rather than with the
/// batch, by where each starts in its text: one the batch's compile deferred
/// compiles when it first runs, one carrying <c>OPTION (RECOMPILE)</c> every
/// time it does.
/// </summary>
internal sealed class StatementsCompiledOnRun
{
    private readonly Dictionary<int, bool> pending = [];
    private HashSet<int>? compiled;

    /// <summary>Where the compile stopped short; every statement starting past it compiles on its first run.</summary>
    public int From = int.MaxValue;

    public void Add(int start, bool everyRun) => this.pending[start] = everyRun;

    /// <summary>Whether the statement starting at <paramref name="start"/> compiles as it runs this time.</summary>
    public bool CompilesNow(int start)
    {
        if (this.pending.TryGetValue(start, out var everyRun))
        {
            if (!everyRun)
            {
                _ = this.pending.Remove(start);
                _ = (this.compiled ??= []).Add(start);
            }
            return true;
        }
        return start > this.From && (this.compiled ??= []).Add(start);
    }
}
