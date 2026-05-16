using System.Text.Json;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ISJSON(expression)</c>: returns <c>int</c> <c>1</c> when the
/// string argument parses as valid JSON (object, array, scalar, or null),
/// <c>0</c> when it doesn't, and SQL NULL when the input itself is NULL.
/// Non-string arguments return 0 — real SQL Server raises Msg 8116 for
/// non-string types, but the simulator's tolerance here is harmless for
/// the bacpac-loader use case (CHECK constraints like
/// <c>isjson([CustomFields])&lt;&gt;0</c>) and matches the lax-mode
/// disposition the simulator uses for related JSON scalars.
/// </summary>
/// <remarks>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/isjson-transact-sql.
/// Optional second argument <c>(VALUE | ARRAY | OBJECT | SCALAR)</c> for
/// shape-checking isn't modeled — DACFx-emitted CHECK constraints use the
/// 1-arg form. Surfaces as Msg 102 at parse if a comma slips in.
/// </remarks>
internal sealed class IsJson(ParserContext context) : Expression
{
    private readonly Expression operand = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.operand.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(SqlType.Int32);

        if (!SqlType.IsStringCategory(value.Type))
            return SqlValue.FromInt32(0);

        try
        {
            using var doc = JsonDocument.Parse(value.AsString);
            return SqlValue.FromInt32(1);
        }
        catch (JsonException)
        {
            return SqlValue.FromInt32(0);
        }
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"ISJSON({this.operand.DebugDisplay()})";
}
