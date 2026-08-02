using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>STRING_ESCAPE(text, 'json')</c>: returns the input with JSON
/// special characters escaped (<c>"</c> → <c>\"</c>, <c>\\</c>,
/// <c>/</c>, <c>\\b</c>, <c>\\f</c>, <c>\\n</c>, <c>\\r</c>, <c>\\t</c>,
/// and control characters as <c>\uHHHH</c>). Real SQL Server documents
/// only <c>'json'</c> as a valid escape mode. NULL input returns NULL.
/// Result type is <see cref="SqlType.NVarcharMax"/> (<c>nvarchar(max)</c>,
/// matching real SQL Server) — escaping can more than double the input, so
/// the result must stream as PLP rather than tripping the bounded wire prefix.
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
        StringScalars.RejectLegacyLob(v, "string_escape");
        if (v.IsNull)
            return SqlValue.Null(SqlType.NVarcharMax);
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
        return SqlValue.FromNVarchar(SqlType.NVarcharMax, sb.ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // STRING_ESCAPE rewrites characters without comparing any, so an
        // unresolved collation rides through to the result (probe-confirmed).
        var textType = StringScalars.BindArgument(this.textArg, batch, resolveColumnType, "string_escape", propagatesUnresolvedCollation: true);
        return UnresolvedCollation.On(textType) is { } conflict ? conflict.Mark(SqlType.NVarcharMax) : SqlType.NVarcharMax;
    }

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
        StringScalars.RejectLegacyLob(input, "translate", argumentIndex: 1);
        StringScalars.RejectLegacyLob(chars, "translate", argumentIndex: 2);
        StringScalars.RejectLegacyLob(translations, "translate", argumentIndex: 3);
        var resultType = ResolveResultType(input.Type);
        if (input.IsNull || chars.IsNull || translations.IsNull)
            return SqlValue.Null(resultType);
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
        return SqlValue.FromString(resultType, sb.ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(BindArguments(batch, resolveColumnType));

    /// <summary>
    /// Compile-time mirror of the three <c>RejectLegacyLob</c> calls in
    /// <see cref="Run"/>, keeping the same argument numbering. Returns the
    /// input's type, which the result type derives from.
    /// </summary>
    private SqlType BindArguments(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var inputType = StringScalars.BindArgument(this.inputArg, batch, resolveColumnType, "translate");
        _ = StringScalars.BindArgument(this.charsArg, batch, resolveColumnType, "translate", argumentIndex: 2);
        _ = StringScalars.BindArgument(this.translationsArg, batch, resolveColumnType, "translate", argumentIndex: 3);
        return inputType;
    }

    /// <summary>
    /// TRANSLATE returns a value of the same length family as its input. A
    /// MAX-form input (<c>varchar(max)</c> / <c>nvarchar(max)</c>) carries
    /// unbounded length through to the result, so it must project as
    /// <see cref="SqlType.NVarcharMax"/> to
    /// stream over the wire as PLP; a bounded input keeps the existing
    /// length-0 <c>nvarchar</c> shape (the simulator coerces every input to
    /// nvarchar before processing — a minor pre-existing family divergence
    /// from real, which preserves the varchar family for varchar input).
    /// </summary>
    private static NVarcharSqlType ResolveResultType(SqlType inputType) =>
        inputType.IsLob
            || inputType is NVarcharSqlType { length: SqlType.MaxLengthSentinel }
            || inputType is VarcharSqlType { length: SqlType.MaxLengthSentinel }
            ? SqlType.NVarcharMax
            : SqlType.NVarchar;

    internal override string DebugDisplay() => $"TRANSLATE({this.inputArg.DebugDisplay()}, {this.charsArg.DebugDisplay()}, {this.translationsArg.DebugDisplay()})";
}
