using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SOUNDEX(s)</c>: returns the 4-character SOUNDEX phonetic
/// encoding of an English-language input. Algorithm: keep the first
/// letter (uppercased), then encode each subsequent consonant via the
/// standard SOUNDEX digit map (B/F/P/V=1, C/G/J/K/Q/S/X/Z=2, D/T=3,
/// L=4, M/N=5, R=6), skipping vowels (A/E/I/O/U/Y) and H/W; runs of
/// identical-code letters collapse to one digit; result is padded with
/// <c>0</c> or truncated to length 4. Empty input returns
/// <c>'0000'</c>; NULL returns NULL.
/// </summary>
internal sealed class Soundex : Expression
{
    private readonly Expression input;

    public Soundex(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.input.Run(runtime);
        StringScalars.RejectLegacyLob(v, "soundex");
        return v.IsNull
            ? SqlValue.Null(SqlType.Varchar)
            : SqlValue.FromVarchar(Compute(v.CoerceTo(SqlType.NVarchar).AsString));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = StringScalars.BindArgument(this.input, batch, resolveColumnType, "soundex");
        return SqlType.Varchar;
    }

    internal override string DebugDisplay() => $"SOUNDEX({this.input.DebugDisplay()})";

    internal static string Compute(string source)
    {
        if (string.IsNullOrEmpty(source))
            return "0000";
        var sb = new StringBuilder(4);
        var firstIdx = -1;
        for (var i = 0; i < source.Length; i++)
        {
            if (char.IsLetter(source[i]))
            {
                firstIdx = i;
                break;
            }
        }
        if (firstIdx < 0)
            return "0000";
        var first = char.ToUpperInvariant(source[firstIdx]);
        _ = sb.Append(first);
        var prevCode = Encode(first);
        for (var i = firstIdx + 1; i < source.Length && sb.Length < 4; i++)
        {
            var c = char.ToUpperInvariant(source[i]);
            if (c is 'H' or 'W')
                continue;
            var code = Encode(c);
            if (code == '0')
            {
                prevCode = '0';
                continue;
            }
            if (code != prevCode)
            {
                _ = sb.Append(code);
                prevCode = code;
            }
        }
        while (sb.Length < 4)
            _ = sb.Append('0');
        return sb.ToString();
    }

    private static char Encode(char c) => c switch
    {
        'B' or 'F' or 'P' or 'V' => '1',
        'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
        'D' or 'T' => '3',
        'L' => '4',
        'M' or 'N' => '5',
        'R' => '6',
        _ => '0',
    };
}

/// <summary>
/// SQL <c>DIFFERENCE(s1, s2)</c>: returns an integer 0-4 measuring the
/// similarity of the two strings' SOUNDEX codes. The count is the
/// number of matching positions (out of 4) when the two codes are
/// compared character-by-character. NULL on either side returns NULL.
/// </summary>
internal sealed class Difference : Expression
{
    private readonly Expression left;
    private readonly Expression right;

    public Difference(ParserContext context)
    {
        this.left = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.right = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var l = this.left.Run(runtime);
        var r = this.right.Run(runtime);
        // DIFFERENCE is the family's odd member: a `text` argument implicitly
        // converts to varchar and evaluates, while `ntext` / `image` raise
        // Msg 8116 — where its own SOUNDEX refuses all three (probe-confirmed
        // 2026-07-31).
        StringScalars.RejectLegacyLob(l, "difference", argumentIndex: 1, allowAnsiText: true);
        StringScalars.RejectLegacyLob(r, "difference", argumentIndex: 2, allowAnsiText: true);
        if (l.IsNull || r.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var sl = Soundex.Compute(l.CoerceTo(SqlType.NVarchar).AsString);
        var sr = Soundex.Compute(r.CoerceTo(SqlType.NVarchar).AsString);
        var matches = 0;
        for (var i = 0; i < 4; i++)
        {
            if (sl[i] == sr[i])
                matches++;
        }
        return SqlValue.FromInt32(matches);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // DIFFERENCE is the one member that takes a `text` argument — it
        // converts implicitly and evaluates — so both slots carry the
        // ANSI-text carve-out its Run does.
        _ = StringScalars.BindArgument(this.left, batch, resolveColumnType, "difference", allowAnsiText: true);
        _ = StringScalars.BindArgument(this.right, batch, resolveColumnType, "difference", argumentIndex: 2, allowAnsiText: true);
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => $"DIFFERENCE({this.left.DebugDisplay()}, {this.right.DebugDisplay()})";
}

/// <summary>
/// SQL <c>STR(float [, length [, decimals]])</c>: right-aligned
/// fixed-width numeric-to-string conversion. Default length is 10;
/// default decimals is 0 (rounds, not truncates). Negative or
/// excessive numbers that don't fit in <c>length</c> render as a
/// string of <c>*</c> characters. NULL input returns NULL. The projected
/// result type is <c>varchar(length)</c> — the <c>length</c> argument
/// (default 10) capped at 8000 and floored at 1 when it is a constant,
/// else the <c>varchar(8000)</c> container. Probe-confirmed against SQL
/// Server 2025 (2026-07-22): <c>STR(3.14159, 6, 2)</c> → <c>varchar(6)</c>,
/// <c>STR(x)</c> → <c>varchar(10)</c>, a variable length → <c>varchar(8000)</c>.
/// </summary>
internal sealed class Str : Expression
{
    private readonly Expression numArg;
    private readonly Expression? lengthArg;
    private readonly Expression? decimalsArg;

    public Str(ParserContext context)
    {
        this.numArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            this.lengthArg = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Tokens.Operator { Character: ',' })
                this.decimalsArg = Parse(context.MoveNextRequiredReturnSelf());
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Project the same bounded varchar type GetSqlType reports so the
        // value's declared width matches the result-set schema.
        var resultType = (VarcharSqlType)StringScalars.SizedResultType(SqlType.Varchar, this.ProjectedLength(), runtime.Batch);
        var v = this.numArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(resultType);
        var num = v.CoerceTo(SqlType.Float).AsDouble;
        var length = this.lengthArg is null ? 10 : StringScalars.CoerceLengthArgument(this.lengthArg.Run(runtime));
        var decimals = this.decimalsArg is null ? 0 : StringScalars.CoerceLengthArgument(this.decimalsArg.Run(runtime));
        if (length < 1)
            length = 1;
        if (decimals < 0)
            decimals = 0;
        var formatted = Math.Round(num, decimals, MidpointRounding.AwayFromZero)
            .ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return formatted.Length > length
            ? SqlValue.FromVarchar(resultType, new string('*', length))
            : SqlValue.FromVarchar(resultType, formatted.PadLeft(length));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.SizedResultType(SqlType.Varchar, this.ProjectedLength(), batch);

    /// <summary>
    /// The projected result width: 10 with no <c>length</c> argument, the
    /// constant integer value of a literal <c>length</c> (later clamped to
    /// 1..8000 by <see cref="StringScalars.SizedResultType"/>), or 8000 for a
    /// non-constant length (matching real's <c>varchar(8000)</c> container).
    /// </summary>
    private int ProjectedLength()
    {
        if (this.lengthArg is null)
            return 10;
        if (this.lengthArg is Value { Constant: { IsNull: false } constant }
            && (SqlType.IsIntegerCategory(constant.Type) || constant.Type is DecimalSqlType || SqlType.IsMoneyCategory(constant.Type)))
        {
            try
            {
                return constant.CoerceTo(SqlType.Int32).AsInt32;
            }
            catch (OverflowException)
            {
                return 8000;
            }
        }

        return 8000;
    }

    internal override string DebugDisplay() => $"STR({this.numArg.DebugDisplay()})";
}
