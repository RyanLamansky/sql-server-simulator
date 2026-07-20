using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// Which DML actions a trigger fires on. Real SQL Server's <c>CREATE
/// TRIGGER tr ON t AFTER INSERT, UPDATE</c> combines multiple actions on
/// a single trigger; the flags enum mirrors that.
/// </summary>
[Flags]
internal enum TriggerActions
{
    None = 0,
    Insert = 1,
    Update = 2,
    Delete = 4,
}

/// <summary>
/// Whether the trigger fires AFTER the DML's heap writes or INSTEAD OF
/// them. AFTER (and its <c>FOR</c> synonym) runs the trigger body
/// post-write with INSERTED / DELETED reflecting committed values;
/// INSTEAD OF replaces the DML entirely — the heap is not written and
/// the trigger body is fully responsible for any side effects. AFTER
/// is permitted on heap tables only; INSTEAD OF is permitted on heap
/// tables and views.
/// </summary>
internal enum TriggerTiming
{
    After,
    InsteadOf,
}

/// <summary>
/// One user-defined trigger. Created via <c>CREATE [OR ALTER] TRIGGER
/// [schema.]name ON [schema.]parent { AFTER | FOR | INSTEAD OF } { INSERT
/// | UPDATE | DELETE } [, ...] AS &lt;body&gt;</c>, dropped via
/// <c>DROP TRIGGER [schema.]name</c>, toggled via <c>DISABLE TRIGGER name
/// ON parent</c> / <c>ENABLE TRIGGER name ON parent</c>, invoked
/// automatically by the matching DML statement (INSERT / UPDATE / DELETE /
/// MERGE).
/// </summary>
/// <remarks>
/// <para>
/// The trigger NAME lives in the schema namespace alongside tables /
/// views / functions / procs (Msg 2714 on collision). The trigger holds
/// a reference to its parent — a <see cref="HeapTable"/> (AFTER or
/// INSTEAD OF) or a <see cref="View"/> (INSTEAD OF only). DML against
/// the parent looks up attached triggers via the schema's
/// <see cref="Schema.Triggers"/> dict + parent-reference match.
/// </para>
/// <para>
/// Body source is captured at CREATE time and re-tokenized per call
/// inside a fresh child <see cref="Parser.BatchContext"/> with a
/// <see cref="Parser.TriggerFrame"/> seeded with the INSERTED / DELETED
/// pseudo-table rowsets. The body sees the parent batch's connection /
/// transaction / undo log state — a body-side throw rolls back the
/// surrounding DML statement.
/// </para>
/// </remarks>
internal sealed class Trigger(
    Schema schema,
    string name,
    int objectId,
    SchemaObject parent,
    TriggerActions actions,
    TriggerTiming timing,
    string bodyText,
    DateTime createDate,
    int bodyLineOffset = 0)
    : SchemaObject(name, objectId, schema.SchemaId, createDate)
{
    public Schema Schema = schema;

    /// <summary>
    /// Number of newlines in the <c>CREATE TRIGGER</c> text preceding
    /// <see cref="BodyText"/>'s start — added to a body error's line so it
    /// reports the whole-<c>CREATE</c>-relative line, mirroring
    /// <see cref="Procedure.BodyLineOffset"/>. Unlike procedures, real SQL
    /// Server attributes a trigger-body error to the trigger's *unqualified*
    /// name (probe-confirmed: <c>ERROR_PROCEDURE()</c> / <c>SqlError.Procedure</c>
    /// return <c>tr_name</c>, not <c>dbo.tr_name</c>). Threaded onto the
    /// per-fire child batch's <see cref="Parser.BatchContext.LineOffset"/>.
    /// </summary>
    public readonly int BodyLineOffset = bodyLineOffset;

    public override string ObjectTypeCode => "TR";
    public override string ObjectTypeDescription => "SQL_TRIGGER";

    /// <summary>
    /// The table or view this trigger is attached to. Always one of
    /// <see cref="HeapTable"/> (AFTER or INSTEAD OF) or <see cref="View"/>
    /// (INSTEAD OF only) — both are <see cref="SchemaObject"/>s so
    /// <c>sys.objects.parent_object_id</c> reads through
    /// <see cref="SchemaObject.ObjectId"/> directly. DML against the
    /// parent at runtime walks the schema's <see cref="Schema.Triggers"/>
    /// dict and fires every trigger whose <see cref="Parent"/> matches
    /// and whose <see cref="Actions"/> include the current DML kind.
    /// </summary>
    public readonly SchemaObject Parent = parent;

    /// <summary>
    /// The set of DML actions this trigger fires on. A single trigger
    /// may handle multiple kinds (<c>AFTER INSERT, UPDATE</c>); the body
    /// uses <c>EXISTS (SELECT 1 FROM INSERTED) / EXISTS (SELECT 1 FROM
    /// DELETED)</c> patterns to discriminate at runtime.
    /// </summary>
    public readonly TriggerActions Actions = actions;

    public readonly TriggerTiming Timing = timing;

    /// <summary>
    /// Raw source text of the body (everything after <c>AS</c>). Re-tokenized
    /// and re-parsed per fire — mirrors <see cref="Procedure.BodyText"/>.
    /// </summary>
    public readonly string BodyText = bodyText;

    /// <summary>
    /// True when the trigger is disabled via <c>DISABLE TRIGGER … ON
    /// parent</c>; disabled triggers don't fire on DML against the parent
    /// but remain in the schema (re-enabled via <c>ENABLE TRIGGER …
    /// ON parent</c>). Probe-confirmed.
    /// </summary>
    public bool IsDisabled;
}
