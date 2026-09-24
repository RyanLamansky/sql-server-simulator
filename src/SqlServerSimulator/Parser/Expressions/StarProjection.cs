using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Placeholder expression for <c>*</c> (or <c>&lt;qualifier&gt;.*</c>) in a
/// SELECT projection list. Replaced by per-column <see cref="Reference"/>
/// expressions in <see cref="Selection"/> after the FROM clause is parsed and
/// the source columns are known. One that survives into typing had no FROM
/// clause to expand against — see <see cref="Unexpandable"/>.
/// </summary>
internal sealed class StarProjection(string? qualifier) : Expression
{
    public readonly string? Qualifier = qualifier;

    public override SqlValue Run(RuntimeContext runtime) => throw this.Unexpandable();

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => throw this.Unexpandable();

    /// <summary>
    /// A star still unexpanded when it is typed had no FROM clause to expand
    /// against: Msg 263 bare, Msg 107 qualified (probed 2026-09-24 against
    /// SQL Server 2025).
    /// </summary>
    internal SimulatedSqlException Unexpandable() =>
        this.Qualifier is { } qualifier
            ? SimulatedSqlException.ColumnPrefixDoesNotMatch(qualifier)
            : SimulatedSqlException.MustSpecifyTableToSelectFrom();

    internal override string DebugDisplay() => this.Qualifier is { } q ? $"{q}.*" : "*";

    internal override void Describe(NodeShape shape) => shape.Local(this.Qualifier);
}
