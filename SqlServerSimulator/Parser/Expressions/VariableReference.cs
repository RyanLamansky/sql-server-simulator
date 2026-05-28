using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// A reference to a per-batch scalar variable (declared via <c>DECLARE</c>
/// or seeded from a SqlClient parameter). Captures the variable's name at
/// parse time and looks the slot up through <c>runtime.Batch</c> on each
/// <see cref="Run"/> call. The name-based lookup is what lets a cached
/// <see cref="Selection"/> replay under a fresh <see cref="BatchContext"/>
/// (the plan cache's reason to exist) — a slot reference captured at parse
/// time would bind to the parsing batch's <c>Variables</c> dict forever
/// and project the original parameter value on every replay. Intra-batch
/// <c>SET</c> / <c>SELECT @v = expr</c> mutations still surface because the
/// slot returned by the runtime lookup is the same one those statements
/// mutate. The parse-time <see cref="ParserContext.Batch"/>.GetVariableSlot
/// call still runs to validate the variable was declared (Msg 137 surfaces
/// at parse, not at first execution); the returned slot also feeds
/// <see cref="GetSqlType"/>, which only runs at parse time and so safely
/// captures parse-time <see cref="VariableSlot.DeclaredType"/>.
/// </summary>
internal sealed class VariableReference : Expression
{
    private readonly string variableName;
    private readonly SqlType declaredType;

    public VariableReference(AtPrefixedString atPrefixed, ParserContext context)
    {
        // Strip the leading '@' to match the Variables-dict key convention.
        var raw = atPrefixed.Value;
        this.variableName = raw.StartsWith('@') ? raw[1..] : raw;
        // Parse-time validation (and capture of the declared type for
        // GetSqlType) — this is what raises Msg 137 if @v was never declared.
        this.declaredType = context.Batch.GetVariableSlot(raw).DeclaredType;
    }

    public override SqlValue Run(RuntimeContext runtime) => runtime.Batch.Variables[this.variableName].Value;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.declaredType;

    internal override string DebugDisplay() => $"@{this.variableName}";
}
