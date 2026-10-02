using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LOWER(x)</c>: lowercases each character. Mirror of
/// <see cref="Upper"/>.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/lower-transact-sql</remarks>
internal sealed class Lower(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    internal override bool ParallelSafe => this.source.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var raw = source.Run(runtime);
        StringScalars.RejectLegacyLob(raw, "lower");
        if (raw.IsNull)
            return SqlValue.Null(StringScalars.CaseMappedType(raw.Type, runtime.Batch));
        var value = StringScalars.CoerceToVarchar(raw, runtime.Batch, "lower");
        var lowered = (value.Type.Collation ?? Collation.Baseline).CaseMapping().ToLower(value.AsString);
        return SqlValue.FromString(StringScalars.CaseMappedType(value.Type, runtime.Batch), lowered);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.CaseMappedType(StringScalars.BindSource(source, batch, resolveColumnType, "lower", coerced: false), batch);

    internal override string DebugDisplay() => $"LOWER({source.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.source);
}
