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
        if (batch.FunctionBodyShape is { } shape)
            shape.Violations.Add((batch.CurrentStatement.StartLine, SimulatedSqlException.SideEffectingOperatorInFunction(operatorName, state)));
    }

    /// <summary>
    /// Records a DML statement's write. A write to a <em>table variable</em> is
    /// legal inside a function (probe-confirmed for both a scalar UDF's own
    /// <c>DECLARE @t TABLE</c> and a multi-statement TVF's return table), so
    /// only a write reaching a persistent table is a violation.
    /// </summary>
    public static void NoteTableWrite(BatchContext batch, string operatorName, HeapTable? table)
    {
        if (table is not { IsTableVariable: true })
            NoteSideEffect(batch, operatorName, StatementOperatorState);
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
