using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>MAX</c> and <c>MIN</c>: tracks the running extreme via
/// <see cref="SqlValue.CompareTo"/>. NULL operands are skipped; empty input
/// returns NULL of the operand type. The DISTINCT keyword is honored at
/// parse time but doesn't affect the result (DISTINCT extremes equal plain
/// extremes; SQL Server accepts the keyword as a no-op here).
/// </summary>
internal sealed class MinMaxAggregator(SqlType resultType, bool isMax) : Aggregator
{
    private SqlValue current = SqlValue.Null(resultType);
    private bool sawAny;

    public override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;
        if (!this.sawAny)
        {
            this.current = value;
            this.sawAny = true;
            return;
        }
        var cmp = value.CompareTo(this.current);
        if ((isMax && cmp > 0) || (!isMax && cmp < 0))
            this.current = value;
    }

    public override SqlValue Result() => this.sawAny ? this.current : SqlValue.Null(resultType);
}
