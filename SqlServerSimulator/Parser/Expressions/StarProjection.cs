using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Placeholder expression for <c>*</c> (or <c>&lt;qualifier&gt;.*</c>) in a
/// SELECT projection list. Replaced by per-column <see cref="Reference"/>
/// expressions in <see cref="Selection"/> after the FROM clause is parsed and
/// the source columns are known. The placeholder can't participate in any
/// other expression context — <see cref="Run"/> / <see cref="GetSqlType"/>
/// throw if it ever survives into evaluation (e.g. <c>WHERE *</c>,
/// <c>SELECT * + 1</c>, or <c>SELECT *</c> with no FROM).
/// </summary>
internal sealed class StarProjection(string? qualifier) : Expression
{
    public readonly string? Qualifier = qualifier;

    public override SqlValue Run(RuntimeContext runtime) =>
        throw new NotSupportedException("'*' is only valid as a top-level SELECT projection element.");

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) =>
        throw new NotSupportedException("'*' is only valid as a top-level SELECT projection element.");

    internal override string DebugDisplay() => this.Qualifier is { } q ? $"{q}.*" : "*";
}
