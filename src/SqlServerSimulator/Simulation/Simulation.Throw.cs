using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and dispatches a <c>THROW</c> statement in both forms:
    /// <list type="bullet">
    /// <item><c>THROW;</c> — no args. Re-raises the in-flight error from
    /// the enclosing CATCH; must be inside a CATCH (Msg 10704 otherwise,
    /// probe-confirmed). Reconstructs the <see cref="SimulatedSqlException"/>
    /// from <see cref="BatchContext.InFlightError"/>.</item>
    /// <item><c>THROW number, message, state;</c> — value form. Raises a
    /// new <see cref="SimulatedSqlException"/> with the supplied number
    /// (50000-2147483647, else Msg 35100), message, and state. Severity is
    /// always class 16 per real SQL Server — probe-confirmed against SQL
    /// Server 2025 (2026-05-12) that <c>THROW 50001, 'custom', 7</c> reports
    /// Class 16 State 7.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// In a TRY frame the exception is caught by the per-statement dispatch
    /// wrapper and stored into <see cref="BatchContext.InFlightError"/>;
    /// outside a TRY frame it propagates out of the batch.
    /// </para>
    /// <para>
    /// Skip-mode gating: <c>THROW;</c> in a skipped branch is a compile-time
    /// check (Msg 10704 — fires even from un-taken IF branches matching the
    /// pattern of Msg 135 / 178). The runtime raise itself is gated on
    /// <c>!IsSkipping</c>.
    /// </para>
    /// <para>
    /// Each value-form argument is a literal or a variable, nothing else —
    /// probe-confirmed 2026-09-23 against SQL Server 2025: an expression,
    /// parentheses or a unary <c>+</c> is Msg 102, <c>NULL</c> Msg 156, and a
    /// number or state literal that isn't an <c>int</c> Msg 1080, while a
    /// leading <c>-</c> on a literal is accepted. A variable converts the way
    /// <c>CAST</c> would (<c>int</c> for the number and state, <c>nvarchar</c>
    /// for the message), and a NULL one reads as 0 or the empty string. A
    /// negative state is Msg 2756; otherwise only its low byte is kept, so 256
    /// raises state 0 and 300 state 44. Formatted-message arguments (<c>%d</c> / <c>%s</c>
    /// placeholders) — real SQL Server's <c>FORMATMESSAGE</c>-style
    /// substitution — aren't modeled in this bundle; defer to a follow-on
    /// alongside <c>RAISERROR</c>.
    /// </para>
    /// </remarks>
    private static void ParseThrowStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextOptional(); // consume THROW

        // Re-raise form: bare THROW followed by a statement boundary.
        if (IsStatementBoundary(context.Token))
        {
            // Compile-time check (fires even in skipped branches, matching
            // real SQL Server's behavior for Msg 178 / Msg 135).
            if (batch.CatchDepth == 0)
                throw SimulatedSqlException.ThrowMustBeInsideCatch();

            if (batch.IsSkipping)
                return;

            // Reconstruct the in-flight error and re-raise. Inside a CATCH
            // InFlightError is non-null by construction (the CATCH only ran
            // because the matching TRY caught something).
            var err = batch.InFlightError!.Value;
            throw SimulatedSqlException.ThrowReRaised(err.Number, err.Message, err.State, err.Line, err.Procedure);
        }

        // Value form: three comma-separated literals or variables.
        var number = ParseThrowArgument(context, isMessage: false);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var message = ParseThrowArgument(context, isMessage: true);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var state = ParseThrowArgument(context, isMessage: false);

        // Real parses the whole statement before running any of it, so a stray
        // token after the argument list is Msg 102 — not the error this THROW
        // would otherwise have raised (probe-confirmed 2026-08-06:
        // `THROW 50000, 'm', 1 zzz` reports near 'zzz', never message 'm').
        // The dispatch loop's own trailing-token check runs too late here,
        // since a raising statement never returns to it.
        if (!IsStatementBoundary(context.Token))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (batch.IsSkipping)
            return;

        var numberValue = Cast.CoerceToDeclared(number.Read(), SqlType.Int32);
        var messageValue = Cast.CoerceToDeclared(message.Read(), SqlType.NVarchar);
        var stateValue = Cast.CoerceToDeclared(state.Read(), SqlType.Int32);
        var numberInt = numberValue.IsNull ? 0 : numberValue.AsInt32;
        if (numberInt < 50000)
            throw SimulatedSqlException.ThrowNumberOutOfRange(numberInt);
        var stateInt = stateValue.IsNull ? 0 : stateValue.AsInt32;
        if (stateInt < 0)
            throw SimulatedSqlException.ThrowStateNegative(stateInt);

        throw SimulatedSqlException.ThrowRaised(numberInt, messageValue.IsNull ? "" : messageValue.AsString, (byte)stateInt);
    }

    /// <summary>
    /// One value-form <c>THROW</c> argument: a variable's slot, or a literal's
    /// value — a string for the message, an <c>int</c> (optionally negated)
    /// for the number and state. Anything else is the syntax error real
    /// raises; see <see cref="ParseThrowStatement"/>.
    /// </summary>
    private static ThrowArgument ParseThrowArgument(ParserContext context, bool isMessage)
    {
        var negative = !isMessage && context.Token is Operator { Character: '-' };
        if (negative)
            context.MoveNextRequired();

        ThrowArgument argument;
        switch (context.Token)
        {
            case AtPrefixedString variable when !negative:
                argument = new(context.Batch.GetVariableSlot(variable.Value), default);
                break;
            case Literal { Value: { IsNull: false } text } when isMessage && SqlType.IsStringCategory(text.Type):
                argument = new(null, text);
                break;
            case Numeric { Value.IsNull: false } literal when !isMessage:
                // A literal that can't be an int — past its range or carrying a
                // fraction — is Msg 1080, echoing the value as written.
                if (literal.Value.Type == SqlType.BigInt)
                    throw SimulatedSqlException.IntegerValueOutOfRange((negative ? -literal.Value.AsInt64 : literal.Value.AsInt64).ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (literal.Value.Type is DecimalSqlType)
                    throw SimulatedSqlException.IntegerValueOutOfRange(negative ? literal.Value.AsDecimal38.Negate().ToString() : literal.Value.AsDecimal38.ToString());
                argument = new(null, SqlValue.FromInt32(negative ? -literal.Value.AsInt32 : literal.Value.AsInt32));
                break;
            case ReservedKeyword keyword:
                throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        context.MoveNextOptional();
        return argument;
    }

    /// <summary>
    /// A parsed <c>THROW</c> argument: the variable read when the statement
    /// runs, or the literal's value.
    /// </summary>
    private readonly struct ThrowArgument(VariableSlot? slot, SqlValue literal)
    {
        public readonly VariableSlot? Slot = slot;
        public readonly SqlValue Literal = literal;

        public SqlValue Read() => this.Slot is null ? this.Literal : this.Slot.Value;
    }
}
