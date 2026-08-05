using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LEN(x)</c>: number of characters in the source value excluding
/// trailing spaces. Distinct from <c>DATALENGTH</c>, which counts raw bytes.
/// </summary>
/// <remarks>
/// SQL Server's quirk: <c>LEN</c> ignores trailing spaces but not leading
/// spaces. The simulator measures by code unit (<see cref="string.Length"/>)
/// under non-SC collations and by Unicode codepoint (rune count) under
/// <c>_SC_</c>-flagged collations — probe-confirmed against SQL Server
/// 2025: <c>LEN(N'😀')</c> = 2 under default / <c>Latin1_General_100_CI_AS</c>
/// (code units, surrogate pair counts as 2) and = 1 under
/// <c>Latin1_General_100_CI_AS_SC_UTF8</c> (the supplementary codepoint
/// counts as 1).
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/len-transact-sql
/// </remarks>
internal sealed class Length(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    internal override bool ParallelSafe => this.source.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var raw = source.Run(runtime);
        StringScalars.RejectLegacyLob(raw, "len");
        // NULL passes through any string function regardless of its underlying
        // type tag; the simulator's untyped NULL literal carries Type=Int32 so
        // the IsNull check has to come before the IsStringCategory check.
        if (raw.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var value = StringScalars.CoerceToVarchar(raw, runtime.Batch, "len");
        var trimmed = value.AsString.TrimEnd(' ');
        var length = value.Type.Collation?.IsSupplementaryCharacterAware == true
            ? SupplementaryCharacters.CodepointCount(trimmed)
            : trimmed.Length;
        return SqlValue.FromInt32(length);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = StringScalars.BindArgument(source, batch, resolveColumnType, "len");
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => $"LEN({source.DebugDisplay()})";

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit) => source.VisitColumnReferences(visit);
}
