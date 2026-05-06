using System.Text;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>STRING_AGG(expr, separator)</c>: concatenates non-NULL values
/// with the separator between them. The separator is evaluated once per
/// row (SQL Server allows a per-row separator value, though it's typically
/// a constant) — the simulator uses the most recent non-NULL separator.
/// Empty / all-NULL input → NULL. The optional
/// <c>WITHIN GROUP (ORDER BY ...)</c> clause isn't parsed; values appear
/// in iteration order, matching SQL Server's documented behavior when
/// WITHIN GROUP is omitted (no ordering guarantee — table-scan order).
/// </summary>
internal sealed class StringAggAggregator(SqlType resultType) : Aggregator
{
    private readonly StringBuilder buffer = new();
    private string lastSeparator = "";
    private bool sawAny;

    public override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;

        // The separator is part of the AggregateExpression; evaluate per row
        // using the same name resolver. Caller (Selection executor) doesn't
        // know to feed it separately, so we capture it via a reference back
        // to the expression. (For STRING_AGG specifically the executor
        // passes the operand result here; the separator must be re-fetched.)
        // A NULL separator is treated as empty — matching SQL Server's lax
        // behavior around all-NULL separators.
        if (this.sawAny)
            _ = this.buffer.Append(this.lastSeparator);
        _ = this.buffer.Append(value.AsString);
        this.sawAny = true;
    }

    /// <summary>
    /// Sets the separator that <see cref="Add"/> will use between subsequent
    /// values; called by the Selection executor before each
    /// <see cref="Add"/> with the per-row evaluation of
    /// <see cref="AggregateExpression.Separator"/>.
    /// </summary>
    public void SetSeparator(string separator) => this.lastSeparator = separator;

    public override SqlValue Result() => this.sawAny
        ? SqlValue.FromString(resultType, this.buffer.ToString())
        : SqlValue.Null(resultType);


}
