using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A CHECK constraint declared on a <see cref="HeapTable"/>. The simulator
/// evaluates each constraint's <see cref="Predicate"/> per row at INSERT /
/// MERGE time; a result of <c>false</c> rejects the row with Msg 547. SQL
/// Server's three-valued-logic semantics apply: a predicate that evaluates
/// to UNKNOWN (any NULL operand without explicit NULL handling) passes —
/// only an explicit <c>false</c> rejects.
/// </summary>
internal sealed class CheckConstraint(string name, BooleanExpression predicate, string? inlineColumn)
{
    public readonly string Name = name;

    public readonly BooleanExpression Predicate = predicate;

    /// <summary>
    /// For inline column-level CHECK (<c>col int CHECK (...)</c>), the
    /// declaring column's name; the simulator weaves it into Msg 547 as
    /// <c>column 'X'</c>. Null for table-level CHECK constraints, where the
    /// message omits the column suffix — matching real SQL Server.
    /// </summary>
    public readonly string? InlineColumn = inlineColumn;
}
