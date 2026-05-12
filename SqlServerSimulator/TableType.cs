using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One user-defined table type. Created via <c>CREATE TYPE schema.name AS
/// TABLE (column_list)</c>, dropped via <c>DROP TYPE [IF EXISTS] schema.name</c>,
/// consumed by <c>DECLARE @t schema.name</c> and as a <c>READONLY</c>
/// procedure parameter (the table-valued-parameter form). Lives in its
/// owning <see cref="Schema"/>'s <see cref="Schema.TableTypes"/> dict;
/// shares the object-name namespace with tables / views / functions / procs
/// (Msg 2714 on cross-kind collision) but the type-name namespace is
/// separate (Msg 219 on dup type name only).
/// </summary>
/// <remarks>
/// <para>
/// The class stores the column-list "template": the resolved
/// <see cref="HeapColumn"/>s plus the pending key / check / computed
/// specifications. Each consumer site (DECLARE @t / TVP-param binding /
/// ADO.NET Structured materialization) calls <see cref="Clone"/> to produce a
/// fresh <see cref="HeapTable"/> instance with a freshly-allocated
/// <c>object_id</c>. The constraint resolution runs per-clone so the
/// auto-generated constraint names embed the @t's name (matches probe:
/// <c>declare @t dbo.tvp_t_pk</c> produces constraint names like
/// <c>PK__#&lt;hex&gt;__&lt;cols&gt;__&lt;hash&gt;</c>, regenerated per
/// declaration).
/// </para>
/// <para>
/// Restrictions enforced at CREATE TYPE parse time (probe-confirmed against
/// SQL Server 2025): no named constraints (<c>CONSTRAINT name</c> → Msg 156),
/// no foreign keys (<c>REFERENCES</c> → Msg 156), no inline non-unique INDEX
/// clause (deferred — Msg 102 in v1; real SQL Server accepts it). All other
/// column features supported in <c>DECLARE @t TABLE</c> work identically here
/// (IDENTITY / inline + table-level PK / UNIQUE / CHECK / computed columns /
/// rowversion / DEFAULT).
/// </para>
/// <para>
/// References from procedures: <c>DROP TYPE</c> walks every procedure's
/// parameter list and raises Msg 3732 if any parameter references this type
/// (probe-confirmed wording: "Cannot drop type 'X' because it is being
/// referenced by object 'Y'").
/// </para>
/// </remarks>
internal sealed class TableType(
    Schema schema,
    string name,
    int typeTableObjectId,
    int userTypeId,
    DateTime createDate,
    HeapColumn[] columns,
    (KeyConstraintKind Kind, string? Name, int[] FullOrdinals)[] pendingKeys,
    (string? Name, BooleanExpression Predicate, string? InlineColumn)[] pendingChecks)
{
    public readonly Schema Schema = schema;
    public readonly string Name = name;

    /// <summary>
    /// Stable per-database identifier surfacing in
    /// <c>sys.table_types.type_table_object_id</c>. <c>sys.columns</c> joins
    /// this to project per-column rows for the type. Distinct from
    /// <see cref="UserTypeId"/> (which is the type's own identifier in
    /// <c>sys.types</c>).
    /// </summary>
    public readonly int TypeTableObjectId = typeTableObjectId;

    /// <summary>
    /// Per-database <c>user_type_id</c> (allocated via
    /// <see cref="Database.AllocateUserTypeId"/>, starting at 256 to avoid
    /// the system-type id range 0–255). Surfaces in
    /// <c>sys.types.user_type_id</c> and <c>sys.table_types.user_type_id</c>.
    /// </summary>
    public readonly int UserTypeId = userTypeId;

    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// Resolved column shape captured at CREATE TYPE time.
    /// </summary>
    public readonly HeapColumn[] Columns = columns;

    /// <summary>
    /// Pending key (PRIMARY KEY / UNIQUE) specs captured at CREATE TYPE
    /// time. Resolved (via <c>ResolveKeyConstraints</c>) per <see cref="Clone"/>
    /// so each clone gets a fresh constraint-name hash embedding the target
    /// <c>@t</c> name — matching probe: declaring two <c>@t</c> variables of
    /// the same type produces two distinct constraint-name hashes.
    /// </summary>
    public readonly (KeyConstraintKind Kind, string? Name, int[] FullOrdinals)[] PendingKeys = pendingKeys;

    /// <summary>
    /// Pending CHECK specs captured at CREATE TYPE time. Resolved per clone
    /// (same rationale as <see cref="PendingKeys"/>).
    /// </summary>
    public readonly (string? Name, BooleanExpression Predicate, string? InlineColumn)[] PendingChecks = pendingChecks;

    /// <summary>
    /// Materializes a fresh <see cref="HeapTable"/> for one <c>DECLARE @t
    /// MyType</c> / TVP-parameter / Structured-ADO.NET binding. The resulting
    /// table carries <see cref="HeapTable.IsTableVariable"/> = true so DML
    /// routes through the non-transactional / per-statement undo log path
    /// (same as inline-form <c>@t TABLE</c>). Each call allocates a fresh
    /// <c>object_id</c> and regenerates constraint names embedding
    /// <paramref name="fullName"/>; the column shape is shared by reference
    /// (immutable post-CREATE TYPE).
    /// </summary>
    public HeapTable Clone(string fullName, BatchContext batch, bool isTableValuedParameter = false) =>
        new(
            fullName,
            this.Columns,
            batch.CurrentDatabase.AllocateObjectId(),
            schemaId: Database.DboSchemaId,
            createDate: batch.CurrentStatement.UtcNow,
            keyConstraints: Simulation.ResolveKeyConstraints(fullName, this.Columns, this.PendingKeys, batch.CurrentDatabase),
            checkConstraints: Simulation.ResolveCheckConstraints(fullName, this.PendingChecks, batch.CurrentDatabase),
            isTableVariable: true,
            isTableValuedParameter: isTableValuedParameter);
}
