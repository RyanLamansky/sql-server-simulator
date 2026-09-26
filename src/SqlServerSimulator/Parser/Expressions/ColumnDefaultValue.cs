using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>SET col = DEFAULT</c> in an <c>UPDATE</c> or a <c>MERGE</c>'s
/// <c>UPDATE</c> action: the column's default — its DEFAULT constraint or bound
/// <c>CREATE DEFAULT</c> object — evaluated per row, or NULL when it has none.
/// The UPDATE parser builds it unbound, before the target's columns are known,
/// and <see cref="Bind"/> ties it to the column once the SET target resolves.
/// </summary>
internal sealed class ColumnDefaultValue : Expression
{
    private readonly HeapColumn? column;

    private ColumnDefaultValue(HeapColumn? column)
    {
        this.column = column;
    }

    /// <summary>The placeholder the SET list parses to.</summary>
    internal static readonly ColumnDefaultValue Unbound = new(null);

    /// <summary>The value <paramref name="target"/>'s default gives.</summary>
    internal static ColumnDefaultValue Bind(HeapColumn target) => new(target);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var target = this.column ?? throw new InvalidOperationException("SET … = DEFAULT ran before its column was bound.");
        return target.Default is { } defaultExpression
            ? defaultExpression.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), runtime.Batch))
            : SqlValue.Null(target.Type);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.column?.Type ?? throw new NotSupportedException("SET … = DEFAULT is not modeled on this UPDATE shape.");

    internal override string DebugDisplay() => "DEFAULT";

    internal override void Describe(NodeShape shape) { }
}
