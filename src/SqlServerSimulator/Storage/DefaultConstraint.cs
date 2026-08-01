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
internal sealed class DefaultConstraint(string name, Expression expression, int objectId, bool isSystemNamed, string? definition, DateTime createDate)
{
    public readonly string Name = name;

    /// <summary>
    /// UTC creation timestamp — the declaring statement's frozen
    /// <c>UtcNow</c>, so a constraint declared inside <c>CREATE TABLE</c>
    /// shares the table's instant while an <c>ALTER TABLE … ADD CONSTRAINT</c>
    /// carries the later one (probe-confirmed). Surfaces in
    /// <c>sys.objects.create_date</c> and the per-family constraint catalog
    /// view's <c>create_date</c>.
    /// </summary>
    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// UTC modification timestamp — equal to <see cref="CreateDate"/> until an
    /// <c>ALTER TABLE … {NOCHECK|CHECK} CONSTRAINT</c> trust toggle or an
    /// <c>sp_rename</c> of the constraint advances it (both probe-confirmed).
    /// Surfaces in <c>sys.objects.modify_date</c> and the per-family
    /// constraint catalog view's <c>modify_date</c>.
    /// </summary>
    public DateTime ModifyDate = createDate;

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
    /// Text form of <see cref="Expression"/> for
    /// <c>sys.default_constraints.definition</c> — the default expression's
    /// original source syntax wrapped in one paren pair, captured at CREATE /
    /// ALTER time via <c>ParserContext.SourceTextFrom</c>. Deliberately not
    /// re-normalized into SQL Server's canonical form (see the alter-table doc's
    /// Definition columns section). Null when no default was captured.
    /// </summary>
    public readonly string? Definition = definition;
}
