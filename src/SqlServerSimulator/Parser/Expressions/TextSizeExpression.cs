using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@TEXTSIZE</c>: returns the session's <c>SET TEXTSIZE</c> byte
/// cap as <see cref="SqlType.Int32"/>. Default is <c>-1</c> (unlimited —
/// probe-confirmed: a fresh SqlClient connection reads
/// <c>@@TEXTSIZE = -1</c>); <c>SET TEXTSIZE 0</c> or any other negative
/// reads back as <c>4096</c>. Mutated by <c>SET TEXTSIZE &lt;N&gt;</c>.
/// </summary>
internal sealed class TextSizeExpression(ParserContext context) : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(context.Connection.TextSize);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@TEXTSIZE";
}
