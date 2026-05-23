using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>STRING_ESCAPE(text, 'json')</c>: returns the input with JSON
/// special characters escaped (<c>"</c> → <c>\"</c>, <c>\\</c>,
/// <c>/</c>, <c>\\b</c>, <c>\\f</c>, <c>\\n</c>, <c>\\r</c>, <c>\\t</c>,
/// and control characters as <c>\uHHHH</c>). Real SQL Server documents
/// only <c>'json'</c> as a valid escape mode. NULL input returns NULL.
/// Result type is <see cref="SqlType.NVarchar"/>.
/// </summary>
internal sealed class StringEscape : Expression
{
    private readonly Expression textArg;
    private readonly Expression modeArg;

    public StringEscape(ParserContext context)
    {
        this.textArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.modeArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.textArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(SqlType.NVarchar);
        // Mode is validated for shape; only 'json' is documented. The
        // simulator accepts any string value and treats it as json (real
        // SQL Server raises Msg 9806 on unknown mode — minor divergence).
        _ = this.modeArg.Run(runtime);
        var input = v.CoerceTo(SqlType.NVarchar).AsString;
        var sb = new StringBuilder(input.Length + 8);
        foreach (var c in input)
        {
            _ = c switch
            {
                '"' => sb.Append("\\\""),
                '\\' => sb.Append("\\\\"),
                '/' => sb.Append("\\/"),
                '\b' => sb.Append("\\b"),
                '\f' => sb.Append("\\f"),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                '\t' => sb.Append("\\t"),
                _ => c < 0x20
                    ? sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture))
                    : sb.Append(c),
            };
        }
        return SqlValue.FromNVarchar(sb.ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"STRING_ESCAPE({this.textArg.DebugDisplay()}, {this.modeArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>TRANSLATE(input, characters, translations)</c>: returns
/// <c>input</c> with each character in <c>characters</c> replaced by
/// the character at the same position in <c>translations</c>. The
/// second and third arguments must have the same length — Msg 9819
/// otherwise. NULL on any argument returns NULL. Result type is the
/// input string type.
/// </summary>
internal sealed class Translate : Expression
{
    private readonly Expression inputArg;
    private readonly Expression charsArg;
    private readonly Expression translationsArg;

    public Translate(ParserContext context)
    {
        this.inputArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.charsArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.translationsArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var input = this.inputArg.Run(runtime);
        var chars = this.charsArg.Run(runtime);
        var translations = this.translationsArg.Run(runtime);
        if (input.IsNull || chars.IsNull || translations.IsNull)
            return SqlValue.Null(SqlType.NVarchar);
        var charsStr = chars.CoerceTo(SqlType.NVarchar).AsString;
        var transStr = translations.CoerceTo(SqlType.NVarchar).AsString;
        if (charsStr.Length != transStr.Length)
            throw SimulatedSqlException.TranslateUnequalChars();
        var inputStr = input.CoerceTo(SqlType.NVarchar).AsString;
        var sb = new StringBuilder(inputStr.Length);
        foreach (var c in inputStr)
        {
            var idx = charsStr.IndexOf(c, StringComparison.Ordinal);
            _ = idx >= 0 ? sb.Append(transStr[idx]) : sb.Append(c);
        }
        return SqlValue.FromNVarchar(sb.ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"TRANSLATE({this.inputArg.DebugDisplay()}, {this.charsArg.DebugDisplay()}, {this.translationsArg.DebugDisplay()})";
}
