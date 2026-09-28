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
/// <param name="objectName">
/// The object the statement acted on, unqualified; null for an event about the
/// database itself (<c>ALTER_DATABASE_SCOPED_CONFIGURATION</c>), which real
/// reports without <c>ObjectName</c> / <c>ObjectType</c>.
/// </param>
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
/// <param name="roleName">
/// The role an <c>ADD_ROLE_MEMBER</c> / <c>DROP_ROLE_MEMBER</c> event changed,
/// whose member is the object.
/// </param>
/// <param name="ownerName">
/// The new owner an <c>ALTER_AUTHORIZATION_DATABASE</c> event names, emitted
/// after <c>ObjectType</c> (probed 2026-09-27 against SQL Server 2025).
/// </param>
/// <param name="serverLevelDatabase">
/// For a server-level event (<c>CREATE_LOGIN</c>, <c>CREATE_DATABASE</c>, …),
/// which only a server-scope trigger takes: the database a database event
/// names, or the empty string for one that names none. Null for a
/// database-level event.
/// </param>
/// <param name="loginElements">
/// A login event's <c>DefaultLanguage</c> … <c>SID</c> elements, rendered.
/// </param>
/// <param name="maskStart">
/// Where a password literal starts in the batch text, which the event's
/// <c>CommandText</c> reports as <c>'******'</c>; -1 when there is none.
/// </param>
/// <param name="maskEnd">The end of that literal.</param>
internal sealed class DdlEventInfo(
    string eventType,
    string? schemaName,
    string? objectName,
    string? objectType,
    string? targetObjectName = null,
    string? targetObjectType = null,
    string? roleName = null,
    string? ownerName = null,
    string? serverLevelDatabase = null,
    string? loginElements = null,
    int maskStart = -1,
    int maskEnd = -1)
{
    public readonly string EventType = eventType;
    public readonly string? SchemaName = schemaName;
    public readonly string? ObjectName = objectName;
    public readonly string? ObjectType = objectType;
    public readonly string? TargetObjectName = targetObjectName;
    public readonly string? TargetObjectType = targetObjectType;
    public readonly string? RoleName = roleName;
    public readonly string? OwnerName = ownerName;
    public readonly string? ServerLevelDatabase = serverLevelDatabase;
    public readonly string? LoginElements = loginElements;
    public readonly int MaskStart = maskStart;
    public readonly int MaskEnd = maskEnd;
}
