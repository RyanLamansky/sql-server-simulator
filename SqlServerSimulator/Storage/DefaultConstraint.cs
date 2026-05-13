using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Named <c>DEFAULT</c> constraint bound to a column on a heap table. Created
/// by either <c>CREATE TABLE</c> inline default (<c>col int DEFAULT 0</c>, auto-named
/// <c>DF__t__col__hash</c>, <see cref="IsSystemNamed"/> = true) or
/// <c>ALTER TABLE … ADD CONSTRAINT df1 DEFAULT (expr) FOR col</c>. Lives on
/// <see cref="HeapColumn.DefaultConstraint"/> — one default per column max
/// (Msg 1781 on a second add).
/// </summary>
/// <remarks>
/// A column with a default constraint has both <see cref="HeapColumn.Default"/>
/// (the expression, read by INSERT to backfill omitted values) and this
/// metadata wrapper (the constraint identity, surfaced through
/// <c>sys.default_constraints</c> and lookupable by name for <c>ALTER TABLE
/// DROP CONSTRAINT</c>). The two move in lockstep: <c>DROP CONSTRAINT</c>
/// clears both; <c>ADD CONSTRAINT … DEFAULT</c> sets both.
/// </remarks>
internal sealed class DefaultConstraint(string name, Expression expression, int objectId, bool isSystemNamed, string? definition)
{
    public readonly string Name = name;

    public readonly Expression Expression = expression;

    /// <summary>
    /// Per-database object identifier — allocated at CREATE TABLE / ALTER
    /// TABLE time. Surfaces in <c>sys.objects</c> as a <c>D </c> row and in
    /// <c>sys.default_constraints</c> as <c>object_id</c>.
    /// </summary>
    public readonly int ObjectId = objectId;

    /// <summary>
    /// True when the name was auto-generated (inline DEFAULT at CREATE TABLE,
    /// or anonymous ALTER TABLE ADD DEFAULT). Surfaces in
    /// <c>sys.default_constraints.is_system_named</c>.
    /// </summary>
    public readonly bool IsSystemNamed = isSystemNamed;

    /// <summary>
    /// Round-trippable text form of <see cref="Expression"/> for
    /// <c>sys.default_constraints.definition</c> (canonical form like
    /// <c>((0))</c> or <c>('abc')</c>). Null when the expression's source
    /// text wasn't captured.
    /// </summary>
    public readonly string? Definition = definition;
}
