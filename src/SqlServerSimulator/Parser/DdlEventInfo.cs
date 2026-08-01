namespace SqlServerSimulator.Parser;

/// <summary>
/// One DDL event a completed statement raised, recorded by the statement's
/// own processor and consumed by <c>Simulation.FireDdlTriggers</c> once the
/// statement finishes. Each instance becomes one <c>EVENT_INSTANCE</c>
/// document a matching database-scope DDL trigger reads through
/// <c>EVENTDATA()</c>.
/// </summary>
/// <remarks>
/// A statement can raise several events — <c>DROP TABLE a, b</c> raises one
/// <c>DROP_TABLE</c> per name (probe-confirmed, each carrying the whole
/// statement's text as <c>CommandText</c>) — so the pending slot on
/// <see cref="StatementContext.PendingDdlEvents"/> is a list.
/// </remarks>
/// <param name="eventType">
/// The <c>sys.trigger_event_types</c> leaf name (<c>CREATE_TABLE</c>,
/// <c>ALTER_INDEX</c>, …), matched against each trigger's declared events.
/// </param>
/// <param name="schemaName">
/// The owning schema, or null for a securable with no schema — real omits the
/// <c>SchemaName</c> element entirely for <c>CREATE_USER</c> / <c>CREATE_ROLE</c>.
/// </param>
/// <param name="objectName">The object the statement acted on, unqualified.</param>
/// <param name="objectType">
/// Real's <c>ObjectType</c> spelling — <c>TABLE</c>, <c>VIEW</c>, <c>INDEX</c>,
/// <c>SQL USER</c>, … (probe-confirmed per event).
/// </param>
/// <param name="targetObjectName">
/// The object the acted-on object hangs off, for the kinds real reports one:
/// an index's or trigger's table, a synonym's base object.
/// </param>
/// <param name="targetObjectType">
/// The target's kind (<c>TABLE</c> / <c>VIEW</c>), emitted alongside
/// <paramref name="targetObjectName"/>. Null where real emits the name without
/// a type — a synonym's base object.
/// </param>
internal sealed class DdlEventInfo(
    string eventType,
    string? schemaName,
    string objectName,
    string objectType,
    string? targetObjectName = null,
    string? targetObjectType = null)
{
    public readonly string EventType = eventType;
    public readonly string? SchemaName = schemaName;
    public readonly string ObjectName = objectName;
    public readonly string ObjectType = objectType;
    public readonly string? TargetObjectName = targetObjectName;
    public readonly string? TargetObjectType = targetObjectType;
}
