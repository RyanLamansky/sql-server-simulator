using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Placeholder expression for <c>*</c> (or <c>&lt;qualifier&gt;.*</c>) in a
/// SELECT projection list. Replaced by per-column <see cref="Reference"/>
/// expressions in <see cref="Selection"/> after the FROM clause is parsed and
/// the source columns are known. The placeholder can't participate in any
/// other expression context — <see cref="Run"/> / <see cref="GetSqlType"/>
/// raise <c>Msg 102</c> if it ever survives into evaluation (e.g.
/// <c>WHERE *</c>, <c>SELECT 1 + *</c>, or <c>SELECT *</c> with no FROM),
/// matching real SQL Server's parse-time rejection.
/// </summary>
internal sealed class StarProjection(string? qualifier) : Expression
{
    public readonly string? Qualifier = qualifier;

    public override SqlValue Run(RuntimeContext runtime) =>
        throw SimulatedSqlException.SyntaxErrorNear('*');

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        throw SimulatedSqlException.SyntaxErrorNear('*');

    internal override string DebugDisplay() => this.Qualifier is { } q ? $"{q}.*" : "*";
}
