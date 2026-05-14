namespace SqlServerSimulator;

/// <summary>
/// One database-scope DDL trigger created via
/// <c>CREATE TRIGGER [schema.]name ON DATABASE FOR &lt;event_type_group&gt; AS &lt;body&gt;</c>.
/// Stored on <see cref="Database.DdlTriggers"/> rather than a per-schema
/// dict because the parent scope is the database itself
/// (<c>parent_class = 0</c> in <c>sys.triggers</c>) — DDL triggers don't
/// belong to any single schema.
/// </summary>
/// <remarks>
/// The simulator <strong>does not</strong> fire DDL triggers. Statements
/// that would invoke the trigger in real SQL Server (CREATE / ALTER / DROP
/// against a database-scope event) execute normally and the trigger's body
/// is never re-tokenized. The element exists to (a) accept the
/// <c>CREATE TRIGGER … ON DATABASE</c> grammar that BACPAC SqlPackage emits
/// from AdventureWorks's <c>[ddlDatabaseTriggerLog]</c>, and (b) populate
/// the catalog views (<c>sys.triggers</c>, <c>sys.sql_modules</c>,
/// <c>sys.trigger_events</c>) so model.xml round-trip works.
/// </remarks>
internal sealed class DdlTrigger(
    string name,
    int objectId,
    int schemaId,
    List<string> eventTypes,
    string bodyText,
    DateTime createDate)
    : SchemaObject(name, objectId, schemaId, createDate)
{
    public override string ObjectTypeCode => "TR";
    public override string ObjectTypeDescription => "SQL_TRIGGER";

    /// <summary>
    /// The set of DDL event types this trigger would fire on. Stored as
    /// raw uppercase identifier strings (<c>DDL_DATABASE_LEVEL_EVENTS</c>,
    /// <c>CREATE_TABLE</c>, <c>DROP_PROCEDURE</c>, …) matching the AW
    /// emit shape. The simulator never fires DDL triggers; this list
    /// exists for <c>sys.trigger_events</c> round-trip.
    /// </summary>
    public readonly List<string> EventTypes = eventTypes;

    /// <summary>
    /// Raw source text of the body (everything after <c>AS</c>). Captured
    /// for <c>sys.sql_modules.definition</c> only — never re-tokenized
    /// because DDL events don't fire in the simulator.
    /// </summary>
    public readonly string BodyText = bodyText;

    /// <summary>
    /// True when the trigger is disabled via <c>DISABLE TRIGGER … ON
    /// DATABASE</c>. Affects only the <c>sys.triggers.is_disabled</c>
    /// surface; firing isn't modeled regardless.
    /// </summary>
    public bool IsDisabled;
}
