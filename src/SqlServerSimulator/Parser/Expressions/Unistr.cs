using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL Server 2025's <c>UNISTR(character_expression [, unicode_escape_character])</c>:
/// the input with each escape — <c>\XXXX</c> a UTF-16 code unit, <c>\+XXXXXX</c>
/// a code point, <c>\\</c> the escape character itself — replaced, in the
/// input's own family and length. A char-family input must carry a
/// UTF-8 collation (Msg 9844); the escape character must be one printable
/// ASCII character (Msg 9843) other than <c>+</c>, a quote, space or a hexit
/// (Msg 9842); a malformed escape is Msg 9841, at state 3 when the escape
/// character ends the input (probed 2026-09-26 against SQL Server 2025).
/// </summary>
internal sealed class Unistr : Expression
{
    private readonly Expression input;
    private readonly Expression? escape;

    public Unistr(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
            this.escape = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.input.Run(runtime);
        var resultType = ResultTypeFor(value.Type);
        var escapeCharacter = '\\';
        if (this.escape is not null)
        {
            var escapeValue = this.escape.Run(runtime);
            if (escapeValue.IsNull)
                return SqlValue.Null(resultType);
            var written = escapeValue.AsString;
            if (written.Length != 1 || written[0] is < ' ' or > '~')
                throw SimulatedSqlException.UnistrEscapeCharacterInvalid(written);
            escapeCharacter = written[0];
            if (escapeCharacter is '+' or '\'' or '"' or ' ' || char.IsAsciiHexDigit(escapeCharacter))
                throw SimulatedSqlException.UnistrEscapeCharacterRefused();
        }
        return value.IsNull ? SqlValue.Null(resultType) : SqlValue.FromString(resultType, Unescape(value.AsString, escapeCharacter));
    }

    private static string Unescape(string text, char escapeCharacter)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != escapeCharacter)
            {
                _ = result.Append(text[i]);
                continue;
            }
            if (i + 1 >= text.Length)
                throw SimulatedSqlException.UnistrEscapeSequenceInvalid(state: 3);
            if (text[i + 1] == escapeCharacter)
            {
                _ = result.Append(escapeCharacter);
                i++;
                continue;
            }
            var wide = text[i + 1] == '+';
            var digits = wide ? 6 : 4;
            var from = wide ? i + 2 : i + 1;
            if (from + digits > text.Length
                || !int.TryParse(text.AsSpan(from, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                || (wide && code > 0x10FFFF))
            {
                throw SimulatedSqlException.UnistrEscapeSequenceInvalid(state: 1);
            }
            if (wide && code > 0xFFFF)
                _ = result.Append(char.ConvertFromUtf32(code));
            else
                _ = result.Append((char)code);
            i = from + digits - 1;
        }
        return result.ToString();
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var inputType = StringScalars.RequireStringArgument(this.input, StringScalars.BindArgument(this.input, batch, resolveColumnType, "unistr"), "unistr", 1, acceptsLegacyLob: false);
        if (inputType is VarcharSqlType or CharSqlType && inputType.Collation?.AnsiCodePage != 65001)
            throw SimulatedSqlException.UnistrRequiresUtf8();
        if (this.escape is not null)
            _ = StringScalars.RequireStringArgument(this.escape, StringScalars.BindArgument(this.escape, batch, resolveColumnType, "unistr", argumentIndex: 2), "unistr", 2, acceptsLegacyLob: false);
        return ResultTypeFor(inputType);
    }

    // The input's family — varchar over a UTF-8 char-family input, nvarchar
    // otherwise — at its length, in its collation.
    private static SqlType ResultTypeFor(SqlType inputType)
    {
        var width = StringScalars.DeclaredWidth(inputType) is > 0 and var declared ? declared : SqlType.MaxLengthSentinel;
        var collation = inputType.Collation ?? Collation.Baseline;
        return inputType is VarcharSqlType or CharSqlType
            ? VarcharSqlType.Get(width, collation, inputType.Coercibility)
            : NVarcharSqlType.Get(width == SqlType.MaxLengthSentinel ? width : Math.Min(width, 4000), collation, inputType.Coercibility);
    }

    internal override string DebugDisplay() => this.escape is null
        ? $"UNISTR({this.input.DebugDisplay()})"
        : $"UNISTR({this.input.DebugDisplay()}, {this.escape.DebugDisplay()})";

    internal override void Describe(NodeShape shape)
    {
        _ = shape.Child(this.input);
        if (this.escape is not null)
            _ = shape.Child(this.escape);
    }
}
