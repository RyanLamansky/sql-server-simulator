using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>Which side(s) a <see cref="Trim"/> strips.</summary>
internal enum TrimSide
{
    /// <summary>Both ends — bare <c>TRIM(x)</c> and the explicit <c>BOTH</c> form.</summary>
    Both,

    /// <summary>Leading only — <c>TRIM(LEADING chars FROM x)</c>.</summary>
    Leading,

    /// <summary>Trailing only — <c>TRIM(TRAILING chars FROM x)</c>.</summary>
    Trailing,
}

/// <summary>
/// SQL <c>TRIM</c> in all its forms:
/// <list type="bullet">
/// <item><c>TRIM(x)</c> — strips leading and trailing spaces.</item>
/// <item><c>TRIM(chars FROM x)</c> — strips any of the characters in
/// <c>chars</c> (a set, not a substring) from both ends.</item>
/// <item><c>TRIM([LEADING|TRAILING|BOTH] chars FROM x)</c> — the ANSI form
/// restricting the side.</item>
/// </list>
/// Probe-confirmed against SQL Server 2025: the trim characters form a set
/// (<c>TRIM('ab' FROM 'abxba')</c> → <c>'x'</c>); a NULL <c>chars</c> or source
/// yields NULL; a side keyword makes <c>chars FROM</c> mandatory
/// (<c>TRIM(LEADING FROM x)</c> → Msg 156 near FROM).
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/trim-transact-sql
/// </summary>
internal sealed class Trim : Expression
{
    private readonly Expression source;
    private readonly Expression? trimChars;
    private readonly TrimSide side;

    public Trim(ParserContext context)
    {
        // Optional side keyword — LEADING / TRAILING / BOTH aren't reserved, so
        // they arrive as identifiers and must be recognized before the operand
        // parse would treat them as a column reference.
        var sawSide = false;
        this.side = TrimSide.Both;
        if (context.Token is UnquotedString sideToken && TryParseSide(sideToken.Span, out var parsedSide))
        {
            this.side = parsedSide;
            sawSide = true;
            context.MoveNextRequired();
        }

        // With a side keyword the `chars FROM` prefix is mandatory: a bare
        // `TRIM(LEADING FROM x)` is Msg 156 near FROM (probe-confirmed).
        if (sawSide && context.Token is ReservedKeyword { Keyword: Keyword.From } bareFrom)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(bareFrom);

        var first = Parse(context);
        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
        {
            this.trimChars = first;
            context.MoveNextRequired();
            this.source = Parse(context);
        }
        else if (sawSide)
        {
            // A side keyword without the `chars FROM x` body.
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        else
        {
            this.source = first;
        }
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Argument numbering follows the written order, so the bare `TRIM(x)`
        // form makes the source argument 1 while `TRIM(chars FROM x)` makes
        // chars argument 1 and the source argument 2 (probe-confirmed
        // 2026-07-31, together with the capitalized `Trim` function word real
        // echoes where the rest of the string family is lowercase). Written
        // order also decides which of two offending arguments is reported, so
        // the character set is gated before the source rather than where it is
        // consumed below — `TRIM(<ntext> FROM <text>)` names argument 1.
        var sourceIndex = this.trimChars is null ? 1 : 2;
        SqlValue? charsValue = null;
        if (this.trimChars is not null)
        {
            charsValue = this.trimChars.Run(runtime);
            StringScalars.RejectLegacyLob(charsValue.Value, "Trim", argumentIndex: 1);
        }
        var value = source.Run(runtime);
        StringScalars.RejectLegacyLob(value, "Trim", sourceIndex);
        if (value.IsNull)
            return SqlValue.Null(value.Type);
        if (!SqlType.IsStringCategory(value.Type))
            throw SimulatedSqlException.InvalidArgumentDataType(value.Type.SqlServerName, sourceIndex, "Trim");

        char[] chars;
        if (charsValue is { } suppliedChars)
        {
            if (suppliedChars.IsNull)
                return SqlValue.Null(value.Type);
            chars = suppliedChars.AsString.ToCharArray();
            // An empty trim-character set removes nothing.
            if (chars.Length == 0)
                return SqlValue.FromString(value.Type, value.AsString);
        }
        else
        {
            chars = [' '];
        }

        var text = value.AsString;
        var trimmed = this.side switch
        {
            TrimSide.Leading => text.TrimStart(chars),
            TrimSide.Trailing => text.TrimEnd(chars),
            _ => text.Trim(chars),
        };
        return SqlValue.FromString(value.Type, trimmed);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.trimChars is not null)
            _ = StringScalars.BindArgument(this.trimChars, batch, resolveColumnType, "Trim", argumentIndex: 1);
        return StringScalars.BindArgument(source, batch, resolveColumnType, "Trim", argumentIndex: this.trimChars is null ? 1 : 2);
    }

    private static bool TryParseSide(ReadOnlySpan<char> span, out TrimSide side)
    {
        if (span.Equals("leading", StringComparison.OrdinalIgnoreCase))
        {
            side = TrimSide.Leading;
            return true;
        }
        if (span.Equals("trailing", StringComparison.OrdinalIgnoreCase))
        {
            side = TrimSide.Trailing;
            return true;
        }
        if (span.Equals("both", StringComparison.OrdinalIgnoreCase))
        {
            side = TrimSide.Both;
            return true;
        }
        side = TrimSide.Both;
        return false;
    }

    internal override string DebugDisplay() => $"TRIM({source.DebugDisplay()})";
}
