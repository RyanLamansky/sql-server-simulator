using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and dispatches a <c>RAISERROR</c> statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grammar: <c>RAISERROR ( msg , severity , state [, arg]... ) [WITH option[, option]...]</c>
    /// where each of <c>msg</c> / <c>severity</c> / <c>state</c> / <c>arg</c>
    /// is a literal token, a signed numeric literal, an <c>@variable</c>
    /// reference, or <c>NULL</c>. Arbitrary expressions (e.g. <c>CAST(...)</c>,
    /// function calls, arithmetic) are rejected at parse time — real SQL
    /// Server's grammar is the same restriction (probe-confirmed: <c>CAST</c>
    /// inside a RAISERROR arg position raises Msg 102). Options are <c>LOG</c>,
    /// <c>NOWAIT</c>, and <c>SETERROR</c>, comma-separated.
    /// </para>
    /// <para>
    /// Severity routing (probe-confirmed against SQL Server 2025 (2026-05-12)):
    /// </para>
    /// <list type="bullet">
    /// <item>NULL / negative severity → treated as 0 (informational, no
    /// error path, message discarded — same as PRINT).</item>
    /// <item>Severity 0-10 → informational. Doesn't throw; doesn't enter
    /// <c>TRY/CATCH</c>; doesn't update <c>@@ERROR</c> unless
    /// <c>WITH SETERROR</c> forces it to 50000.</item>
    /// <item>Severity 11-18 → catchable error. Throws
    /// <see cref="SimulatedSqlException"/> with <c>Class = severity</c>,
    /// <c>Number = 50000</c>, <c>State = state</c>.</item>
    /// <item>Severity 19-25 → Msg 2754 (requires sysadmin + <c>WITH LOG</c> —
    /// the simulator has no principal model and matches the probe's
    /// non-sysadmin behavior here, since apps connecting as a non-sysadmin
    /// service account see the same wall on real SQL Server).</item>
    /// <item>Severity &gt; 25 → Msg 2754 (same path as 19-25).</item>
    /// </list>
    /// <para>
    /// State clamping: NULL / out-of-range (&gt; 255) silently clamps to 0
    /// (probe-confirmed — real SQL Server doesn't raise, just substitutes 0).
    /// </para>
    /// <para>
    /// <c>msg</c> dispatch: a string-typed value (or NULL — rendered as a
    /// single space, matching probe) is the inline-message form, formatted
    /// via <see cref="MessageFormatter"/> with the substitution args. A
    /// numeric value is treated as a registered <c>sys.messages</c> id —
    /// the simulator hasn't modeled the message registry, so every numeric
    /// msg_id falls into one of two error paths: <c>&lt; 13000</c> or
    /// <c>= 50000</c> raises Msg 2732 (the "invalid number" path —
    /// 50000 is reserved as the synthesized id for inline-string raises,
    /// so passing it literally is rejected), and any other numeric id raises
    /// Msg 18054 (the "not found in sys.messages" path). Apps using
    /// <c>RAISERROR(N, ...)</c> with N in the valid user-defined range work
    /// on real SQL Server only after <c>sp_addmessage</c> registration;
    /// they hit Msg 18054 here for the same reason
    /// (probe-confirmed wording verbatim).
    /// </para>
    /// <para>
    /// <c>WITH</c> option handling (probe-confirmed): <c>LOG</c> raises Msg
    /// 2778 (always — non-sysadmin connections see the same on real SQL
    /// Server); <c>NOWAIT</c> is accepted and ignored (no streaming model);
    /// <c>SETERROR</c> forces <c>@@ERROR</c> to 50000 for sev ≤ 10 (sev ≥ 11
    /// already populates <c>@@ERROR</c> through the standard error path).
    /// </para>
    /// </remarks>
    private static void ParseRaiserrorStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume RAISERROR
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var msgValue = ParseRaiserrorArgument(batch);
        ExpectComma(context);
        var severityValue = ParseRaiserrorArgument(batch);
        ExpectComma(context);
        var stateValue = ParseRaiserrorArgument(batch);

        var substitutions = new List<SqlValue>();
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            substitutions.Add(ParseRaiserrorArgument(batch));
        }
        if (substitutions.Count > 20)
            throw SimulatedSqlException.RaiserrorTooManySubstitutionParameters();

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var withLog = false;
        var withNowait = false;
        var withSetError = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            while (true)
            {
                switch (context.Token)
                {
                    case UnquotedString { ContextualKeyword: ContextualKeyword.Log }:
                        withLog = true;
                        break;
                    case UnquotedString { ContextualKeyword: ContextualKeyword.NoWait }:
                        withNowait = true;
                        break;
                    case UnquotedString { ContextualKeyword: ContextualKeyword.SetError }:
                        withSetError = true;
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                context.MoveNextOptional();
                if (context.Token is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
        }
        _ = withNowait; // accepted and ignored — no streaming model.

        if (batch.IsSkipping)
            return;

        // WITH LOG without sysadmin: probe-confirmed Msg 2778, fires even
        // before severity validation (probe shows it from sev=10).
        if (withLog)
            throw SimulatedSqlException.RaiserrorLogRequiresSysadmin();

        // Severity: NULL or negative → 0 (informational, no error).
        var severity = CoerceToInt32OrNull(severityValue) is { } sev && sev >= 0 ? sev : 0;
        if (severity > 18)
            throw SimulatedSqlException.RaiserrorSeverityRequiresSysadmin();

        // State: NULL or out-of-range → 0. Real SQL Server's accepted range
        // is 0-255 (per docs) but values outside silently clamp (probe).
        var state = (byte)(CoerceToInt32OrNull(stateValue) is { } st && st is >= 0 and <= 255 ? st : 0);

        // Resolve the message: string-typed values are inline format strings;
        // numeric values are msg_id lookups against the (unmodeled) registry.
        string formatString;
        if (msgValue.IsNull)
        {
            // NULL message renders as a single space (probe).
            formatString = " ";
        }
        else if (msgValue.Type.Category == SqlTypeCategory.String)
        {
            formatString = msgValue.AsString;
            // Empty string also renders as a single space (probe-confirmed).
            if (formatString.Length == 0)
                formatString = " ";
        }
        else
        {
            var msgId = CoerceToInt32OrNull(msgValue) ?? 0;
            if (msgId is 50000 or < 13000)
                throw SimulatedSqlException.RaiserrorMsgIdInvalid(msgId);
            // Any other numeric id — even valid user-defined ranges or system
            // ids like 13001 — falls into the "registry not modeled" path.
            throw SimulatedSqlException.RaiserrorMsgIdNotFound(msgId, (byte)severity, state);
        }

        var formatted = MessageFormatter.Format(formatString, substitutions);

        if (severity >= 11)
        {
            // Catchable error. The TRY/CATCH dispatch wrapper handles the
            // Class / State / Number capture from this exception.
            throw SimulatedSqlException.RaiserrorRaised(formatted, (byte)severity, state);
        }

        // Informational severity (0-10): no throw. The message routes through
        // the connection's InfoMessage event (severity / state / number 50000
        // captured on the SimulatedError); coalesces with any PRINTs in the
        // same batch. WITH SETERROR forces @@ERROR to 50000 for the next
        // statement to observe.
        batch.AppendInfoError(@class: (byte)severity, state: state, number: 50000, message: formatted);
        if (withSetError)
        {
            batch.Connection.LastErrorNumber = 50000;
            batch.CurrentStatement.SuppressErrorReset = true;
        }
    }

    /// <summary>
    /// Parses one RAISERROR argument value at the current cursor. Accepted
    /// forms (matching real SQL Server's grammar, probe-confirmed via
    /// Msg 102 on <c>CAST</c> in arg position): a string / numeric
    /// <see cref="Literal"/>, a signed <see cref="Numeric"/> literal, an
    /// <c>@variable</c> reference (read as its current value), or the
    /// <c>NULL</c> keyword. Leaves the cursor on the first un-consumed
    /// token (the trailing <c>,</c> or <c>)</c>).
    /// </summary>
    private static SqlValue ParseRaiserrorArgument(BatchContext batch)
    {
        var context = batch.Parser;
        var negate = false;
        switch (context.Token)
        {
            case Operator { Character: '-' }:
                negate = true;
                context.MoveNextRequired();
                break;
            case Operator { Character: '+' }:
                context.MoveNextRequired();
                break;
        }

        SqlValue value;
        switch (context.Token)
        {
            case Literal lit:
                if (negate) throw SimulatedSqlException.SyntaxErrorNear(context);
                value = lit.Value;
                break;
            case Numeric num:
                value = negate ? NegateNumeric(num.Value) : num.Value;
                break;
            case ReservedKeyword { Keyword: Keyword.Null }:
                if (negate) throw SimulatedSqlException.SyntaxErrorNear(context);
                value = SqlValue.Null(SqlType.Int32);
                break;
            case AtPrefixedString varRef:
                if (negate) throw SimulatedSqlException.SyntaxErrorNear(context);
                value = batch.GetVariableSlot(varRef.Value).Value;
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return value;
    }

    private static SqlValue NegateNumeric(SqlValue v) =>
        v.IsNull ? v
        : v.Type == SqlType.Int32 ? SqlValue.FromInt32(-v.AsInt32)
        : v.Type == SqlType.BigInt ? SqlValue.FromInt64(-v.AsInt64)
        : v.Type == SqlType.SmallInt ? SqlValue.FromInt16((short)-v.AsInt32)
        : v;

    /// <summary>
    /// Coerces a RAISERROR control argument (severity / state / numeric msg_id)
    /// to an int. Accepts the int family (tinyint/smallint/int) and bigint;
    /// other types fall back to NULL so the caller can apply the standard
    /// "NULL → 0" rule (probe-confirmed: NULL severity / state silently
    /// behaves as 0, not an error).
    /// </summary>
    private static int? CoerceToInt32OrNull(SqlValue value) =>
        value.IsNull ? null
        : value.Type == SqlType.Int32 ? value.AsInt32
        : value.Type == SqlType.SmallInt ? value.AsInt16
        : value.Type == SqlType.TinyInt ? value.AsByte
        : value.Type == SqlType.BigInt ? (int)value.AsInt64
        : null;

    private static void ExpectComma(ParserContext context)
    {
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
    }
}
