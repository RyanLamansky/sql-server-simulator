using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ISJSON(expression [, { VALUE | ARRAY | OBJECT | SCALAR }])</c>:
/// returns <c>int</c> <c>1</c> when the string argument is well-formed JSON of
/// the kind asked for with nothing but whitespace around it, <c>0</c> when it
/// isn't, and SQL NULL when the input itself is NULL. Without the second
/// argument the kind is an object or array; <c>VALUE</c> takes any JSON value,
/// <c>SCALAR</c> a string or number but not <c>true</c> / <c>false</c> /
/// <c>null</c> (probed 2026-09-26 against SQL Server 2025).
/// Non-string arguments return 0 — real SQL Server raises Msg 8116 for
/// non-string types, but the simulator's tolerance here is harmless for
/// the bacpac-loader use case (CHECK constraints like
/// <c>isjson([CustomFields])&lt;&gt;0</c>) and matches the lax-mode
/// disposition the simulator uses for related JSON scalars.
/// </summary>
internal sealed class IsJson : Expression
{
    private readonly Expression operand;

    /// <summary>
    /// The root kinds <see cref="JsonText.RootKind"/> reports that the type
    /// constraint accepts.
    /// </summary>
    private readonly string acceptedKinds = "{[";

    public IsJson(ParserContext context)
    {
        this.operand = Parse(context);
        if (context.Token is not Operator { Character: ',' })
            return;
        this.acceptedKinds = context.GetNextRequired() switch
        {
            UnquotedString word => ConstraintKinds(word.Value) ?? throw SimulatedSqlException.NotARecognizedDatepartOption(word.Value, "isjson"),
            _ => throw SimulatedSqlException.InvalidParameterSpecifiedFor(2, "isjson"),
        };
        context.MoveNextRequired();
    }

    private static string? ConstraintKinds(string word)
    {
        Span<char> folded = stackalloc char[word.Length];
        _ = word.AsSpan().ToUpperInvariant(folded);
        return folded switch
        {
            "ARRAY" => "[",
            "OBJECT" => "{",
            "SCALAR" => "\"",
            "VALUE" => "{[\"l",
            _ => null,
        };
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.operand.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(SqlType.Int32);
        return SqlValue.FromInt32(SqlType.IsStringCategory(value.Type) && JsonText.RootKind(value.AsString) is not '\0' and var kind && this.acceptedKinds.Contains(kind, StringComparison.Ordinal) ? 1 : 0);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = StringScalars.RequireStringArgument(this.operand, this.operand.GetSqlType(batch, resolveColumnType), "isjson", 1, acceptsLegacyLob: false);
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => $"ISJSON({this.operand.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.operand);
}
