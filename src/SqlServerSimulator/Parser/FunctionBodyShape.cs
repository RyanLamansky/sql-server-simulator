using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The structural facts about a function body that decide whether the function
/// may be created at all — real SQL Server's three body-shape rules, which sit
/// beside the name binding rather than inside it:
/// <strong>Msg 455</strong> (the last statement must be <c>RETURN</c>),
/// <strong>Msg 444</strong> (a <c>SELECT</c> returning rows to the client) and
/// <strong>Msg 443</strong> (a side-effecting operator).
/// Populated while <c>Simulation.BindModuleBodyAtCreate</c> walks a scalar-UDF
/// or multi-statement-TVF body — <see cref="BatchContext.FunctionBodyShape"/>
/// is non-null only for that walk, so the recording sites are no-ops on the hot
/// path and on the module kinds real exempts (procedures and triggers).
/// <para>Violations are gathered rather than raised on sight because real
/// reports every binder error in the body <em>before</em> any shape error
/// (probe-confirmed: a <c>PRINT</c> on line 3 and a bad column on line 4 report
/// Msg 207 first, and a body carrying both plus a missing trailing
/// <c>RETURN</c> reports 207, 443, 455 in that order). Gathering through the
/// walk and appending them behind the binder's own errors at the end
/// reproduces that sequence.</para>
/// </summary>
internal sealed class FunctionBodyShape
{
    /// <summary>
    /// Real's state on Msg 443 for a statement that writes data or changes
    /// session state — DML, <c>TRUNCATE TABLE</c>, <c>SELECT … INTO</c>, the
    /// transaction statements, and every <c>SET</c> form.
    /// </summary>
    public const byte StatementOperatorState = 15;

    /// <summary>
    /// Real's state on Msg 443 for a statement that emits to the client or
    /// diverts control — <c>PRINT</c>, <c>RAISERROR</c>, <c>THROW</c>,
    /// <c>WAITFOR</c>, <c>EXEC (…)</c> and the <c>TRY</c> / <c>CATCH</c>
    /// delimiters.
    /// </summary>
    public const byte ControlOperatorState = 14;

    /// <summary>Real's state on Msg 443 for a side-effecting <em>built-in</em> call.</summary>
    public const byte BuiltInOperatorState = 1;

    /// <summary>
    /// Real's state on Msg 443 for a <c>timestamp</c> column in a table a
    /// function declares — its return table or a <c>DECLARE @t TABLE</c> in
    /// its body — which it names as the <c>TIMESTAMP</c> operator (probed
    /// 2026-09-30 against SQL Server 2025).
    /// </summary>
    public const byte TimestampColumnState = 16;

    /// <summary>
    /// Violations in source order — every one of them reaches the <c>CREATE</c>,
    /// as one exception carrying an entry each.
    /// </summary>
    public readonly List<(int Line, SimulatedSqlException Error)> Violations = [];

    /// <summary>
    /// Line of the last statement the walk reached, at <em>any</em> nesting
    /// depth — the line Msg 455 carries. Real reports the innermost trailing
    /// statement's line even when the rule fails because that statement sits
    /// inside an <c>IF</c> or <c>WHILE</c> body (probe-confirmed).
    /// </summary>
    public int LastStatementLine = 1;

    /// <summary>
    /// Whether the last statement reached through <em>bare</em> <c>BEGIN … END</c>
    /// nesting only was a <c>RETURN</c>. A trailing block whose last inner
    /// statement returns satisfies real's rule; a trailing <c>IF</c> or
    /// <c>WHILE</c> never does, however its arms end (probe-confirmed:
    /// <c>IF @x = 1 RETURN 1 ELSE RETURN 2</c> as the final statement is still
    /// Msg 455).
    /// </summary>
    public bool LastStatementIsReturn;

    /// <summary>
    /// Nesting depth inside a construct whose contained statements can't settle
    /// the last-statement rule (an <c>IF</c> or <c>WHILE</c> arm). Bare
    /// <c>BEGIN … END</c> deliberately doesn't count — it is transparent.
    /// </summary>
    public int ConditionalDepth;

    /// <summary>
    /// Whether the statement being dispatched reads from a rowset — a FROM
    /// clause at any nesting depth, or a set operator. Msg 444 carries state 2
    /// when it does and state 3 for a wholly-computed projection
    /// (<c>SELECT 1</c>, <c>SELECT @x</c>); probe-confirmed, including that a
    /// FROM-less <c>SELECT</c> over a subquery that reads takes state 2.
    /// </summary>
    public bool StatementReadsData;

    /// <summary>
    /// True once the walk reached the end of the body. A walk cut short — by a
    /// swallowed deferred-name error or an unmodeled feature — never saw the
    /// real last statement, so the Msg 455 check is left unrun.
    /// </summary>
    public bool WalkCompleted;

    /// <summary>
    /// Records a side-effecting operator at the statement currently being
    /// dispatched. No-ops when <paramref name="batch"/> isn't a function-body
    /// bind, which is every call site's fast path.
    /// </summary>
    public static void NoteSideEffect(BatchContext batch, string operatorName, byte state)
    {
        if (batch.FunctionBodyShape is not { } shape)
            return;
        if (shape.ContextConnection)
        {
            // A built-in is refused under another operator's name there, which
            // isn't modeled; it runs.
            if (state == BuiltInOperatorState)
                return;
            state = ContextConnectionState;
        }

        shape.Violations.Add((batch.CurrentStatement.StartLine, SimulatedSqlException.SideEffectingOperatorInFunction(operatorName, state)));
    }

    /// <summary>
    /// Real's state on Msg 443 for a statement a SQLCLR function runs on its
    /// context connection, whatever the operator (probed 2026-09-28 against
    /// SQL Server 2025).
    /// </summary>
    public const byte ContextConnectionState = 2;

    /// <summary>
    /// True when the walk checks a command a SQLCLR function runs on its
    /// context connection rather than a T-SQL function body: only Msg 443
    /// applies there, at <see cref="ContextConnectionState"/>.
    /// </summary>
    public bool ContextConnection;

    /// <summary>
    /// Records a DML statement's write. A write to a <em>table variable</em> is
    /// legal inside a function (probe-confirmed for both a scalar UDF's own
    /// <c>DECLARE @t TABLE</c> and a multi-statement TVF's return table), so
    /// only a write reaching a persistent table is a violation — or one whose
    /// <c>OUTPUT</c> clause sends rows to the client (see
    /// <see cref="NoteClientOutput"/>).
    /// </summary>
    public static void NoteTableWrite(BatchContext batch, string operatorName, HeapTable? table) =>
        NoteWrite(batch, operatorName, persistent: table is not { IsTableVariable: true });

    /// <summary>
    /// Records a DML statement's write, <paramref name="persistent"/> when it
    /// reaches anything but a table variable.
    /// </summary>
    public static void NoteWrite(BatchContext batch, string operatorName, bool persistent)
    {
        if (batch.FunctionBodyShape is not { } shape)
            return;
        shape.statementWrite = operatorName;
        shape.statementWritePersists = persistent;
        shape.SettleStatementWrite(batch);
    }

    /// <summary>
    /// Records a DML statement's <c>OUTPUT</c> clause without <c>INTO</c>,
    /// which would send the written rows to the client: the write is Msg 443
    /// state 15 under its own verb even when it reaches only a table variable,
    /// once per statement whatever else refuses it (probed 2026-09-30 against
    /// SQL Server 2025, for <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> and
    /// <c>MERGE</c> in both function kinds; a procedure takes it, and
    /// <c>OUTPUT … INTO</c> a table variable is legal).
    /// </summary>
    public static void NoteClientOutput(BatchContext batch)
    {
        if (batch.FunctionBodyShape is not { } shape)
            return;
        shape.statementSendsOutput = true;
        shape.SettleStatementWrite(batch);
    }

    /// <summary>The verb of the write the statement being dispatched makes; null before it names one.</summary>
    private string? statementWrite;
    private bool statementWritePersists, statementSendsOutput, statementWriteRefused;

    /// <summary>Clears what <see cref="NoteWrite"/> and <see cref="NoteClientOutput"/> recorded, as a statement begins.</summary>
    public void BeginStatement()
    {
        this.statementWrite = null;
        this.statementWritePersists = this.statementSendsOutput = this.statementWriteRefused = false;
    }

    /// <summary>
    /// Refuses the statement's write once its verb is known and either it
    /// reaches a persistent object or its rows go to the client — in whichever
    /// order the parse meets the two, since a joined <c>DELETE</c>'s
    /// <c>OUTPUT</c> precedes the <c>FROM</c> that names its target.
    /// </summary>
    private void SettleStatementWrite(BatchContext batch)
    {
        if (this.statementWriteRefused || this.statementWrite is not { } verb || !(this.statementWritePersists || this.statementSendsOutput))
            return;
        this.statementWriteRefused = true;
        NoteSideEffect(batch, verb, StatementOperatorState);
    }

    /// <summary>
    /// Records that the statement being dispatched reads a rowset — the input
    /// to Msg 444's state. Set from the FROM-clause and set-operator parse
    /// sites, so a read at any nesting depth counts.
    /// </summary>
    public static void NoteRowsetRead(ParserContext context)
    {
        if (context.Batch.FunctionBodyShape is { } shape)
            shape.StatementReadsData = true;
    }

    /// <summary>
    /// Records a <c>SELECT</c> statement that would return its rows to the
    /// client. An assignment-only <c>SELECT @v = …</c> is legal;
    /// <c>SELECT … INTO</c> is its own Msg 443 operator and is recorded there.
    /// </summary>
    public static void NoteClientSelect(BatchContext batch)
    {
        if (batch.FunctionBodyShape is { } shape)
            shape.Violations.Add((batch.CurrentStatement.StartLine, SimulatedSqlException.FunctionSelectReturnsDataToClient(shape.StatementReadsData)));
    }

    /// <summary>
    /// Every violation the <c>CREATE</c> should report, in the order real
    /// reports them: the gathered ones in source order, then Msg 455 when the
    /// walk finished on a statement that wasn't a <c>RETURN</c> — last because
    /// its statement is the body's last.
    /// </summary>
    public IEnumerable<(int Line, SimulatedSqlException Error)> AllViolations()
    {
        foreach (var violation in this.Violations)
            yield return violation;
        if (this.WalkCompleted && !this.LastStatementIsReturn)
            yield return (this.LastStatementLine, SimulatedSqlException.FunctionMustEndWithReturn());
    }
}
