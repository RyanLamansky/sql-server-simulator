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
/// <param name="targetQualifierElements">
/// The elements ahead of <c>TargetObjectName</c>, rendered: a synonym
/// event's <c>TargetServerName</c>, <c>TargetDatabaseName</c> and
/// <c>TargetSchemaName</c>.
/// </param>
/// <param name="principalElements">
/// A database principal event's elements after <c>ObjectType</c>, rendered:
/// the principal's <c>DefaultSchema</c>, <c>DefaultLanguage</c> and
/// <c>SID</c> as its kind carries them, ahead of a membership event's
/// <c>RoleName</c>.
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
/// <param name="trailingElements">
/// The elements an event kind carries after the common ones, rendered: a login
/// event's <c>DefaultLanguage</c> … <c>SID</c>, a permission event's
/// <c>Grantor</c> … <c>CascadeOption</c>, an extended-property or binding
/// event's property and <c>Parameters</c> list.
/// </param>
/// <param name="systemProcedureText">
/// For an event a system procedure raises through the statement it runs —
/// <c>sp_addrolemember</c>'s <c>ALTER ROLE</c> — that statement's text, which
/// the event reports with <c>QUOTED_IDENTIFIER</c> on, and under which the
/// triggers it fires run with <c>NOCOUNT</c> on, as inside real's procedure;
/// null for any other.
/// </param>
/// <param name="masks">
/// Where each password literal starts and ends in the batch text, in text
/// order, which the event's <c>CommandText</c> reports as <c>'******'</c>;
/// null when there is none.
/// </param>
internal sealed class DdlEventInfo(
    string eventType,
    string? schemaName,
    string? objectName,
    string? objectType,
    string? targetObjectName = null,
    string? targetObjectType = null,
    string? targetQualifierElements = null,
    string? principalElements = null,
    string? roleName = null,
    string? ownerName = null,
    string? serverLevelDatabase = null,
    string? trailingElements = null,
    (int Start, int End)[]? masks = null,
    string? systemProcedureText = null)
{
    public readonly string EventType = eventType;
    public readonly string? SchemaName = schemaName;
    public readonly string? ObjectName = objectName;
    public readonly string? ObjectType = objectType;
    public readonly string? TargetObjectName = targetObjectName;
    public readonly string? TargetObjectType = targetObjectType;
    public readonly string? TargetQualifierElements = targetQualifierElements;
    public readonly string? PrincipalElements = principalElements;
    public readonly string? RoleName = roleName;
    public readonly string? OwnerName = ownerName;
    public readonly string? ServerLevelDatabase = serverLevelDatabase;
    public readonly string? TrailingElements = trailingElements;
    public readonly (int Start, int End)[]? Masks = masks;
    public readonly string? SystemProcedureText = systemProcedureText;
}
