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
internal sealed class CheckConstraint(string name, BooleanExpression predicate, string? inlineColumn, int objectId)
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

    /// <summary>
    /// Per-database object identifier for this constraint — allocated at
    /// CREATE TABLE alongside the table. Surfaces in <c>sys.objects</c> as
    /// a <c>C</c> row with <c>parent_object_id</c> linking back to the
    /// owning table.
    /// </summary>
    public readonly int ObjectId = objectId;

    /// <summary>
    /// True when the constraint's name was auto-generated rather than
    /// supplied via <c>CONSTRAINT name</c>. Surfaces in
    /// <c>sys.check_constraints.is_system_named</c>.
    /// </summary>
    public bool IsSystemNamed;

    /// <summary>
    /// True iff added via <c>ALTER TABLE … WITH NOCHECK ADD CONSTRAINT</c>
    /// (existing-row validation bypassed). Surfaces in
    /// <c>sys.check_constraints.is_not_trusted</c>. False for CREATE-time
    /// CHECK constraints.
    /// </summary>
    public bool IsNotTrusted;

    /// <summary>
    /// Round-trippable text form of <see cref="Predicate"/> for
    /// <c>sys.check_constraints.definition</c>. Captured from the parser's
    /// source text at parse time (canonical form `([col]&gt;(0))`) — real
    /// SQL Server reformats with the same bracket / paren wrapping.
    /// </summary>
    public string? Definition;
}
