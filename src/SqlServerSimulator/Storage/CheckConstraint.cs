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
internal sealed class CheckConstraint(string name, BooleanExpression predicate, string? inlineColumn, int objectId, DateTime createDate)
{
    /// <summary>Mutable for sp_rename.</summary>
    public string Name = name;

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

    public readonly BooleanExpression Predicate = predicate;

    /// <summary>
    /// Whether <see cref="Predicate"/> calls a user-defined function, T-SQL or
    /// CLR, which can read the table being written. Real judges a CHECK once
    /// the row has landed, so such a function sees the row it judges — and,
    /// in a multi-row write, the rows written before it but not after
    /// (probed 2026-10-07 against SQL Server 2025: <c>CHECK (dbo.cnt() &lt; 3)</c>
    /// over <c>SELECT COUNT(*)</c> refuses the third row, and
    /// <c>CHECK (dbo.maxid() = id)</c> admits <c>VALUES (1), (2), (3)</c> but
    /// not <c>(3), (2), (1)</c>). Only these wait for the write; the rest are
    /// judged before it, where their verdict can't differ.
    /// </summary>
    public readonly bool CallsUserFunction = CallsAnyUserFunction(predicate);

    /// <summary>
    /// The column a column-level CHECK belongs to — the declaring column of an
    /// inline <c>col int CHECK (...)</c>, or the one column a table-level CHECK
    /// reads, which real files the same way. Msg 547 ends <c>column 'X'</c>
    /// for one; null for a CHECK over several columns, where it doesn't.
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
    /// or disabled via <c>ALTER TABLE … NOCHECK CONSTRAINT</c>. Cleared by
    /// <c>ALTER TABLE … WITH CHECK CHECK CONSTRAINT name</c> on successful
    /// re-validation. Surfaces in <c>sys.check_constraints.is_not_trusted</c>.
    /// </summary>
    public bool IsNotTrusted;

    /// <summary>
    /// True iff declared <c>CHECK NOT FOR REPLICATION</c>. Still enforced,
    /// but never trusted — <c>WITH CHECK CHECK CONSTRAINT</c> leaves
    /// <see cref="IsNotTrusted"/> set (probed 2026-09-26 against SQL Server
    /// 2025).
    /// </summary>
    public bool NotForReplication;

    /// <summary>
    /// True iff the CHECK was disabled via <c>ALTER TABLE … NOCHECK
    /// CONSTRAINT name</c>. While disabled, INSERT / UPDATE / MERGE skip
    /// predicate evaluation. Cleared by <c>ALTER TABLE … CHECK CONSTRAINT
    /// name</c>. Surfaces in <c>sys.check_constraints.is_disabled</c>.
    /// </summary>
    public bool IsDisabled;

    /// <summary>
    /// Text form of <see cref="Predicate"/> for
    /// <c>sys.check_constraints.definition</c> — the predicate in SQL Server's
    /// canonical <c>([col]&gt;(0))</c> form, captured at CREATE / ALTER time via
    /// <c>ParserContext.CanonicalDefinitionFrom</c>, or its source text wrapped
    /// in one paren pair where that renderer doesn't reach (see the alter-table
    /// doc's Definition columns section).
    /// </summary>
    public string? Definition;

    private static bool CallsAnyUserFunction(BooleanExpression predicate)
    {
        var calls = false;
        predicate.Walk((node, _) =>
        {
            calls |= node is Parser.Expressions.UserFunctionCall or Parser.Expressions.ClrFunctionCall;
            return !calls;
        });
        return calls;
    }
}
