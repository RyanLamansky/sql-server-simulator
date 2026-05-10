using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// A reference to a per-batch scalar variable (declared via <c>DECLARE</c>
/// or seeded from a SqlClient parameter). Captures the live
/// <see cref="VariableSlot"/> at parse time and reads its current value at
/// runtime — required because <c>SET</c> / <c>SELECT @v = expr</c> mutate
/// the slot between statements within the same batch.
/// </summary>
internal sealed class VariableReference(AtPrefixedString atPrefixed, ParserContext context) : Expression
{
    private readonly VariableSlot slot = context.Batch.GetVariableSlot(atPrefixed.Value);
    private readonly string debugName = atPrefixed.Value;

    public override SqlValue Run(RuntimeContext runtime) => this.slot.Value;

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => this.slot.DeclaredType;

    internal override string DebugDisplay() => $"@{this.debugName}";
}
