using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and runs <c>WAITFOR DELAY</c> or <c>WAITFOR TIME</c>, each taking
    /// a string literal or a <c>@variable</c>. The operand grammar is strict —
    /// <c>cast(...)</c>, integer literals and the bare <c>NULL</c> literal are
    /// syntax errors (probed 2026-05-11 against SQL Server 2025, and for
    /// <c>TIME</c> 2026-10-10 against SQL Server 2025) — and any other word
    /// after <c>WAITFOR</c> is Msg 155.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both forms read their operand identically, as a time of day: a literal
    /// (see <see cref="TryParseWaitForTime"/>) while the batch compiles, so a
    /// malformed one is Msg 148 before anything runs, an untaken branch
    /// included; a variable as it runs, as a <c>datetime</c> whose time of day
    /// is taken (see <see cref="WaitForVariableTimeOfDay"/>). <c>DELAY</c>
    /// waits that long, an empty string or a NULL-valued variable waiting
    /// nothing.
    /// </para>
    /// <para>
    /// <c>TIME</c> waits until the next moment the server's clock reads that
    /// time of day, which is tomorrow once it has passed today, however
    /// recently (see <see cref="WaitForTimeDelay"/>). The server's local clock
    /// is UTC here, so the time is compared with UTC. An empty string is
    /// midnight, as is an <c>int</c> or <c>smallint</c> variable, while a
    /// NULL-valued variable of any accepted type returns at once (probed
    /// 2026-10-10 against SQL Server 2025).
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
        // tokenized as UnquotedString.
        var untilTimeOfDay = context.Token switch
        {
            UnquotedString { ContextualKeyword: ContextualKeyword.Time } => true,
            UnquotedString { ContextualKeyword: ContextualKeyword.Delay } => false,
            UnquotedString word => throw SimulatedSqlException.WaitForOptionNotRecognized(word.Value),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

        context.MoveNextRequired(); // consume DELAY / TIME

        TimeSpan? timeOfDay;
        switch (context.Token)
        {
            case Literal lit when lit.Value.Type is VarcharSqlType or NVarcharSqlType:
                {
                    var text = lit.Value.IsNull ? string.Empty : lit.Value.AsString;
                    if (!TryParseWaitForTime(text, out var parsed))
                        throw SimulatedSqlException.IncorrectWaitForTimeSyntax(text);
                    context.MoveNextOptional();
                    if (batch.IsSkipping)
                        return;
                    timeOfDay = parsed;
                    break;
                }

            case AtPrefixedString variableToken:
                {
                    var slot = batch.GetVariableSlot(variableToken.Value);
                    context.MoveNextOptional();
                    if (batch.IsSkipping)
                        return;
                    timeOfDay = WaitForVariableTimeOfDay(slot);
                    break;
                }

            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (timeOfDay is not { } value)
            return;
        var delay = untilTimeOfDay ? WaitForTimeDelay(value, DateTime.UtcNow) : value;
        if (delay.Ticks > 0)
            WaitInterruptibly(batch, delay);
    }

    /// <summary>
    /// The time of day a <c>WAITFOR</c> variable operand holds, or null when
    /// it is NULL; see <see cref="ParseWaitForStatement"/>. A string one (a MAX
    /// one never converts) failing the literal grammar is the conversion's
    /// Msg 241, which ends the batch; an <c>int</c> or <c>smallint</c> one
    /// counts days, so its time of day is midnight; any other type than those
    /// and <c>datetime</c> is Msg 9815, which ends only its statement, whatever
    /// the value (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static TimeSpan? WaitForVariableTimeOfDay(VariableSlot slot)
    {
        var type = slot.DeclaredType;
        switch (type)
        {
            case VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType:
                if (slot.Value.IsNull)
                    return null;
                if (type is VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel }
                    || !TryParseWaitForTime(slot.Value.AsString, out var timeOfDay))
                {
                    throw SimulatedSqlException.ConversionFailedDateTimeFromString();
                }
                return timeOfDay;
            case Int32SqlType or SmallIntSqlType:
                return slot.Value.IsNull ? null : TimeSpan.Zero;
            case DateTimeSqlType:
                return slot.Value.IsNull ? null : slot.Value.AsDateTime.TimeOfDay;
            default:
                throw SimulatedSqlException.WaitForOperandTypeRefused(SimulatedSqlException.FamilyRootName(type));
        }
    }

    /// <summary>
    /// How long a <c>WAITFOR TIME</c> for <paramref name="timeOfDay"/> waits
    /// when the server's clock reads <paramref name="utcNow"/>: until that time
    /// today, or tomorrow when it has already passed — a target even a few
    /// milliseconds behind the clock waits the day around (probed 2026-10-10
    /// against SQL Server 2025). A target equal to the clock waits nothing.
    /// </summary>
    internal static TimeSpan WaitForTimeDelay(TimeSpan timeOfDay, DateTime utcNow)
    {
        var delay = timeOfDay - utcNow.TimeOfDay;
        return delay.Ticks < 0 ? delay + TimeSpan.FromDays(1) : delay;
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
    /// Reads a <c>WAITFOR DELAY</c> / <c>WAITFOR TIME</c> time of day the way real's
    /// <c>datetime</c> conversion reads a time-only string (probed 2026-09-28
    /// and 2026-10-02 against SQL Server 2025): surrounding blanks ignored,
    /// <c>h:m</c> with an optional <c>:s</c> and a fraction of at most three
    /// digits after <c>.</c> or <c>:</c>, any number of digits per field,
    /// minutes and seconds under 60, and an optional <c>AM</c> / <c>PM</c>
    /// suffix taking an hour of 12 at most (and at least 1 for <c>PM</c>) —
    /// which also stands alone with a bare hour (<c>12 AM</c>). The empty string is
    /// midnight: a zero delay, or a <c>WAITFOR TIME</c> until midnight.
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
