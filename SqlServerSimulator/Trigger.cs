using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

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
/// them. Real SQL Server distinguishes the two at CREATE TRIGGER time;
/// only AFTER is modeled today (the INSTEAD OF path would route DML
/// through the trigger body instead of the table writer, which is
/// structurally different and deferred).
/// </summary>
internal enum TriggerTiming
{
    After,
    InsteadOf,
}

/// <summary>
/// One user-defined trigger. Created via <c>CREATE [OR ALTER] TRIGGER
/// [schema.]name ON [schema.]parent_table { AFTER | FOR } { INSERT |
/// UPDATE | DELETE } [, ...] AS &lt;body&gt;</c>, dropped via
/// <c>DROP TRIGGER [schema.]name</c>, toggled via <c>DISABLE TRIGGER name
/// ON table</c> / <c>ENABLE TRIGGER name ON table</c>, invoked
/// automatically by the matching DML statement (INSERT / UPDATE / DELETE /
/// MERGE).
/// </summary>
/// <remarks>
/// <para>
/// The trigger NAME lives in the schema namespace alongside tables /
/// views / functions / procs (Msg 2714 on collision). The trigger holds
/// a reference to its parent <see cref="HeapTable"/>; DML against that
/// table looks up the table's attached triggers via the schema's
/// <see cref="Schema.Triggers"/> dict + name match against the trigger's
/// ParentTable.
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
    HeapTable parentTable,
    TriggerActions actions,
    TriggerTiming timing,
    string bodyText,
    DateTime createDate)
{
    public readonly Schema Schema = schema;
    public readonly string Name = name;
    public readonly int ObjectId = objectId;

    /// <summary>
    /// The table this trigger is attached to. DML against this table at
    /// runtime walks the schema's <see cref="Schema.Triggers"/> dict and
    /// fires every trigger whose <see cref="ParentTable"/> matches and
    /// whose <see cref="Actions"/> include the current DML kind.
    /// </summary>
    public readonly HeapTable ParentTable = parentTable;

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

    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// True when the trigger is disabled via <c>DISABLE TRIGGER … ON
    /// table</c>; disabled triggers don't fire on DML against the parent
    /// table but remain in the schema (re-enabled via <c>ENABLE TRIGGER
    /// … ON table</c>). Probe-confirmed.
    /// </summary>
    public bool IsDisabled;
}
