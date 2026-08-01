using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// Common metadata base for every named, schema-resident database object —
/// <see cref="HeapTable"/>, <see cref="View"/>,
/// <see cref="Procedure"/>, <see cref="UserDefinedFunction"/>,
/// <see cref="Sequence"/>, <see cref="TableType"/>, <see cref="Trigger"/>,
/// <see cref="Synonym"/>.
/// The base unifies the fields every concrete type was previously
/// declaring independently (and that <c>sys.objects</c> /
/// <c>OBJECT_ID()</c> need to project): <see cref="Name"/>,
/// <see cref="ObjectId"/>, <see cref="SchemaId"/>, <see cref="CreateDate"/>,
/// <see cref="ModifyDate"/>, plus the <c>sys.objects.type</c> /
/// <c>type_desc</c> discriminators.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Trigger.Parent"/> is typed as <c>SchemaObject</c> so a trigger
/// can be attached to a <see cref="HeapTable"/> or a <see cref="View"/>
/// without falling through <c>object</c>. DML hooks that need to dispatch on
/// the trigger's parent take a <c>SchemaObject</c> parameter directly.
/// </para>
/// <para>
/// <see cref="SchemaId"/> is stored as the integer ID for both the
/// <c>SchemaId</c>-only types (<see cref="HeapTable"/>) and the
/// types that also carry a <see cref="Schema"/> reference (every other
/// schema-object kind); those derived types continue to expose their
/// <see cref="Schema"/> field independently — the base just keeps the
/// projection-relevant integer.
/// </para>
/// </remarks>
internal abstract class SchemaObject(string name, int objectId, int schemaId, DateTime createDate)
{
    // Mutable: EXEC sp_rename (table / object rename) reassigns the leaf name
    // in place while keeping the object identity (and ObjectId) stable, matching
    // SQL Server's rename-preserves-object_id semantic. Every other site treats
    // it as effectively immutable.
    public string Name = name;
    public readonly int ObjectId = objectId;

    /// <summary>
    /// Per-object lock resource backing schema-stability (Sch-S) /
    /// schema-modification (Sch-M) acquisition. Every read of this object
    /// (DML, FROM-source, EXEC, NEXT VALUE FOR, DECLARE @t MyType, etc.)
    /// acquires Sch-S for the duration of the statement; every DDL on the
    /// object (DROP / ALTER / TRUNCATE) acquires Sch-M. Owner = the running
    /// <see cref="SimulatedDbConnection"/>. The resource is allocated once
    /// at object construction and lives for the object's lifetime; DROP
    /// discards the object reference entirely, so any pending Sch-S waits
    /// behind the DROP's Sch-M resolve naturally — the post-DROP dict
    /// lookup will miss and the caller surfaces Msg 208.
    /// </summary>
    public readonly LockResource SchemaLock = new();

    /// <summary>
    /// Schema-id of the schema this object lives in. Surfaces in
    /// <c>sys.objects.schema_id</c> / <c>sys.tables.schema_id</c> /
    /// <c>sys.views.schema_id</c> / <c>sys.procedures.schema_id</c>.
    /// Mutable — <c>ALTER SCHEMA dest TRANSFER source.obj</c> reseats the
    /// object into a different schema and updates this field along with
    /// the per-derived-type <c>Schema</c> reference (where present).
    /// </summary>
    public int SchemaId = schemaId;

    /// <summary>
    /// UTC creation timestamp — captured at CREATE time from the executing
    /// statement's frozen UtcNow on
    /// <see cref="Parser.StatementContext"/>. Surfaces in
    /// <c>sys.objects.create_date</c>.
    /// </summary>
    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// UTC modification timestamp — equal to <see cref="CreateDate"/> for a
    /// fresh object; ALTER paths update it (currently only HeapTable
    /// surfaces this; the rest preserve CreateDate). Surfaces in
    /// <c>sys.objects.modify_date</c>.
    /// </summary>
    public DateTime ModifyDate = createDate;

    /// <summary>
    /// Two-character <c>sys.objects.type</c> discriminator: <c>'U '</c>
    /// (USER_TABLE), <c>'V '</c> (VIEW), <c>'P '</c> (SQL_STORED_PROCEDURE),
    /// <c>'FN'</c> (SQL_SCALAR_FUNCTION), <c>'IF'</c>
    /// (SQL_INLINE_TABLE_VALUED_FUNCTION), <c>'TR'</c> (SQL_TRIGGER),
    /// <c>'SO'</c> (SEQUENCE_OBJECT), <c>'TT'</c> (TYPE_TABLE),
    /// <c>'SN'</c> (SYNONYM).
    /// Constants only — concrete types never compute these dynamically.
    /// </summary>
    public abstract string ObjectTypeCode { get; }

    /// <summary>
    /// Long-form <c>sys.objects.type_desc</c> matching
    /// <see cref="ObjectTypeCode"/> (e.g. <c>"USER_TABLE"</c>,
    /// <c>"SQL_TRIGGER"</c>). Probe-confirmed verbatim per concrete type.
    /// </summary>
    public abstract string ObjectTypeDescription { get; }

    /// <summary>
    /// Full original <c>CREATE</c>-statement source text for programmable
    /// modules (procedures, views, triggers, scalar / inline / multi-statement
    /// functions), captured at CREATE / ALTER time and surfaced verbatim by
    /// <c>OBJECT_DEFINITION(object_id)</c> and <c>sys.sql_modules.definition</c>.
    /// Null for non-module objects (tables, sequences, table types) and for
    /// modules created <c>WITH ENCRYPTION</c> (matching SQL Server's NULL).
    /// The leading verb is normalized to <c>CREATE</c> even when the statement
    /// was an <c>ALTER</c> / <c>CREATE OR ALTER</c>, mirroring SQL Server.
    /// </summary>
    public string? DefinitionText;

    /// <summary>
    /// The session <c>QUOTED_IDENTIFIER</c> setting captured when this object
    /// was created, re-stamped by <c>ALTER</c> / <c>CREATE OR ALTER</c>.
    /// A module's body executes under this setting rather than the calling
    /// session's, so a procedure created under <c>OFF</c> keeps reading
    /// <c>"…"</c> as a string literal no matter who invokes it. Surfaces in
    /// <c>sys.sql_modules.uses_quoted_identifier</c> and
    /// <c>OBJECTPROPERTY(id, 'IsQuotedIdentOn' | 'ExecIsQuotedIdentOn')</c>.
    /// Non-module objects leave it at the <see langword="true"/> default:
    /// real answers 1 for a table regardless of the creating session
    /// (probe-confirmed), and a table's computed-column / constraint
    /// expressions are parsed once at CREATE, never re-read.
    /// </summary>
    public bool UsesQuotedIdentifier = true;

    /// <summary>
    /// True for the object kinds that carry a T-SQL module body — the set
    /// <c>sys.sql_modules</c> emits a row for, and the set whose null
    /// <see cref="DefinitionText"/> therefore means <c>WITH ENCRYPTION</c>
    /// rather than "no text at all". A CLR routine is excluded: it has no
    /// T-SQL body, so real SQL Server gives it no <c>sys.sql_modules</c> row
    /// (probe-confirmed) and <c>sp_helptext</c> answers Msg 15197 for it.
    /// </summary>
    public static bool IsSqlModule(SchemaObject? obj) =>
        obj is Procedure or View or Trigger or (UserDefinedFunction and not ClrScalarFunction);
}
