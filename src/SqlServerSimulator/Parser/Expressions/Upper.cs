using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>UPPER(x)</c>: uppercases each character by the argument's
/// collation's own table (<see cref="CaseMap"/>). NULL passes through.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/upper-transact-sql</remarks>
internal sealed class Upper(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    internal override bool ParallelSafe => this.source.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var raw = source.Run(runtime);
        StringScalars.RejectLegacyLob(raw, "upper");
        if (raw.IsNull)
            return SqlValue.Null(StringScalars.ResolveRewrittenType(raw.Type, runtime.Batch));
        var value = StringScalars.CoerceToVarchar(raw, runtime.Batch, "upper");
        var uppered = (value.Type.Collation ?? Collation.Baseline).CaseMapping().ToUpper(value.AsString);
        return SqlValue.FromString(StringScalars.ResolveRewrittenType(value.Type, runtime.Batch), uppered);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.ResolveRewrittenType(StringScalars.BindArgument(source, batch, resolveColumnType, "upper"), batch);

    internal override string DebugDisplay() => $"UPPER({source.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.source);
}
