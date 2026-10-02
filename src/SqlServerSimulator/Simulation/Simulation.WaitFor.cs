using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and runs <c>WAITFOR DELAY '&lt;time&gt;'</c> or
    /// <c>WAITFOR DELAY @variable</c>. The operand grammar is strict — only a
    /// string literal or a <c>@variable</c> reference; <c>cast(...)</c>,
    /// integer literals and the bare <c>NULL</c> literal are syntax errors
    /// (probed 2026-05-11 against SQL Server 2025). <c>WAITFOR TIME</c> (the
    /// absolute-time form) raises <see cref="NotSupportedException"/>, and any
    /// other word there is Msg 155.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal is read as a time of day (see <see cref="TryParseWaitForTime"/>)
    /// while the batch compiles, so a malformed one is Msg 148 before anything
    /// runs, an untaken branch included. A variable is read as it runs, as a
    /// <c>datetime</c> whose time of day is the delay: a string one (a MAX one
    /// never converts) failing the same grammar is the conversion's Msg 241,
    /// an <c>int</c> or <c>smallint</c> one counts days and so waits nothing,
    /// and any other type is Msg 9815, which ends only its statement (probed
    /// 2026-10-02 against SQL Server 2025). An empty string or a NULL-valued
    /// variable is a zero delay.
    /// </para>
    /// <para>
    /// Sleep mechanism: a cancellable wait on the calling thread (see
    /// <see cref="WaitInterruptibly"/>), matching real SQL Server's "blocks
    /// the connection" semantics while staying interruptible by a command
    /// cancel (TDS attention / <c>CommandTimeout</c> / in-process
    /// <c>Cancel()</c>) — the wait wakes early and the batch aborts.
    /// <c>@@ROWCOUNT</c> resets to 0
    /// (probe-confirmed; applied by the dispatcher after this parser returns).
    /// Skip-mode (un-taken IF, after BREAK/CONTINUE/RETURN) suppresses the
    /// sleep entirely.
    /// </para>
    /// </remarks>
    private static void ParseWaitForStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume WAITFOR

        // DELAY and TIME are contextual keywords (not in the reserved list),
        // tokenized as UnquotedString. WAITFOR TIME isn't modeled — it's an
        // absolute-time wait whose primary use case is scheduling.
        switch (context.Token)
        {
            case UnquotedString { ContextualKeyword: ContextualKeyword.Time }:
                throw new NotSupportedException("WAITFOR TIME (absolute-time wait) isn't modeled — WAITFOR DELAY is.");
            case UnquotedString { ContextualKeyword: ContextualKeyword.Delay }:
                break;
            case UnquotedString word:
                throw SimulatedSqlException.WaitForOptionNotRecognized(word.Value);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        context.MoveNextRequired(); // consume DELAY

        TimeSpan delay;
        switch (context.Token)
        {
            case Literal lit when lit.Value.Type is VarcharSqlType or NVarcharSqlType:
                {
                    var text = lit.Value.IsNull ? string.Empty : lit.Value.AsString;
                    if (!TryParseWaitForTime(text, out delay))
                        throw SimulatedSqlException.IncorrectWaitForTimeSyntax(text);
                    context.MoveNextOptional();
                    if (batch.IsSkipping)
                        return;
                    break;
                }

            case AtPrefixedString variableToken:
                {
                    var slot = batch.GetVariableSlot(variableToken.Value);
                    context.MoveNextOptional();
                    if (batch.IsSkipping)
                        return;
                    delay = WaitForVariableDelay(slot);
                    break;
                }

            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (delay.Ticks > 0)
            WaitInterruptibly(batch, delay);
    }

    /// <summary>The delay a <c>WAITFOR DELAY @variable</c> waits; see <see cref="ParseWaitForStatement"/>.</summary>
    private static TimeSpan WaitForVariableDelay(VariableSlot slot)
    {
        var type = slot.DeclaredType;
        switch (type)
        {
            case VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType:
                if (slot.Value.IsNull)
                    return TimeSpan.Zero;
                if (type is VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel }
                    || !TryParseWaitForTime(slot.Value.AsString, out var delay))
                {
                    throw SimulatedSqlException.ConversionFailedDateTimeFromString();
                }
                return delay;
            case Int32SqlType or SmallIntSqlType:
                return TimeSpan.Zero;
            case DateTimeSqlType:
                return slot.Value.IsNull ? TimeSpan.Zero : slot.Value.AsDateTime.TimeOfDay;
            default:
                throw SimulatedSqlException.WaitForOperandTypeRefused(SimulatedSqlException.FamilyRootName(type));
        }
    }

    /// <summary>
    /// Sleeps for <paramref name="delay"/>, but wakes early if the command is
    /// cancelled (a TDS attention from a client <c>SqlCommand.Cancel()</c> /
    /// <c>CommandTimeout</c>, or an in-process <c>Cancel()</c>). This is what
    /// makes <c>WAITFOR DELAY</c> — the canonical cancel target — actually
    /// interruptible: the wait blocks on the execution cancellation token's
    /// wait handle, which the attention watcher signals. On wake the caller
    /// returns and the dispatch loop observes the same cancelled token to
    /// abort the batch. Without an active cancellation scope (a bare
    /// engine-only path) it falls back to a plain sleep.
    /// </summary>
    private static void WaitInterruptibly(BatchContext batch, TimeSpan delay)
    {
        var token = batch.Connection.ExecutionCancellationToken;
        var session = batch.Connection.Session;
        session.WaitStartedTicks = Environment.TickCount64;
        session.InWaitFor = true;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (token.CanBeCanceled)
                _ = token.WaitHandle.WaitOne(delay);
            else
                Thread.Sleep(delay);
        }
        finally
        {
            session.InWaitFor = false;
            batch.Connection.WaitedTicks += System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks;
        }
    }

    /// <summary>
    /// Reads a <c>WAITFOR DELAY</c> time of day the way real's
    /// <c>datetime</c> conversion reads a time-only string (probed 2026-09-28
    /// and 2026-10-02 against SQL Server 2025): surrounding blanks ignored,
    /// <c>h:m</c> with an optional <c>:s</c> and a fraction of at most three
    /// digits after <c>.</c> or <c>:</c>, any number of digits per field,
    /// minutes and seconds under 60, and an optional <c>AM</c> / <c>PM</c>
    /// suffix taking an hour of 12 at most (and at least 1 for <c>PM</c>) —
    /// which also stands alone with a bare hour (<c>12 AM</c>). The empty string is a zero delay.
    /// </summary>
    private static bool TryParseWaitForTime(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var text = value.AsSpan().Trim();
        if (text.IsEmpty)
            return true;

        var meridiem = 0; // 1 = AM, 2 = PM
        if (text.Length >= 2 && (text[^1] is 'm' or 'M') && (text[^2] is 'a' or 'A' or 'p' or 'P'))
        {
            meridiem = text[^2] is 'a' or 'A' ? 1 : 2;
            text = text[..^2].TrimEnd();
        }

        Span<long> fields = stackalloc long[3];
        var fieldCount = 0;
        var fraction = 0L;
        var fractionDigits = -1;
        var i = 0;
        while (true)
        {
            var start = i;
            long field = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                field = Math.Min((field * 10) + (text[i] - '0'), 1_000_000);
                i++;
            }
            if (i == start)
                return false;
            fields[fieldCount++] = field;
            if (i == text.Length)
                break;
            // After the seconds a '.' or ':' opens the fraction.
            if (fieldCount == 3 && text[i] is '.' or ':')
            {
                i++;
                var fractionStart = i;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    fraction = (fraction * 10) + (text[i] - '0');
                    i++;
                }
                fractionDigits = i - fractionStart;
                if (fractionDigits is 0 or > 3 || i != text.Length)
                    return false;
                break;
            }
            if (text[i] != ':' || fieldCount == 3)
                return false;
            i++;
        }

        // A bare number is an hour only beside AM / PM.
        if (fieldCount == 1 && meridiem == 0)
            return false;
        var hours = fields[0];
        var minutes = fieldCount > 1 ? fields[1] : 0;
        var seconds = fieldCount > 2 ? fields[2] : 0;
        if (minutes > 59 || seconds > 59)
            return false;
        if (meridiem != 0)
        {
            // AM takes hours 0 to 12, PM 1 to 12.
            if (hours > 12 || (meridiem == 2 && hours == 0))
                return false;
            hours = (hours % 12) + (meridiem == 2 ? 12 : 0);
        }
        if (hours > 23)
            return false;
        var milliseconds = fractionDigits switch
        {
            1 => fraction * 100,
            2 => fraction * 10,
            3 => fraction,
            _ => 0,
        };
        result = new TimeSpan(0, (int)hours, (int)minutes, (int)seconds, (int)milliseconds);
        return true;
    }
}
