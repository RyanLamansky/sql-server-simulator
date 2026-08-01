using System.Collections.Frozen;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// One database-scope DDL trigger created via
/// <c>CREATE TRIGGER [schema.]name ON DATABASE FOR &lt;event_type_group&gt; AS &lt;body&gt;</c>.
/// Stored on <see cref="Database.DdlTriggers"/> rather than a per-schema
/// dict because the parent scope is the database itself
/// (<c>parent_class = 0</c> in <c>sys.triggers</c>) — DDL triggers don't
/// belong to any single schema.
/// </summary>
/// <remarks>
/// The body fires after a matching DDL statement completes, inside that
/// statement's atomic scope — see <c>Simulation.FireDdlTriggers</c> and
/// <c>docs/claude/triggers.md</c>.
/// </remarks>
internal sealed class DdlTrigger(
    string name,
    int objectId,
    int schemaId,
    List<string> eventTypes,
    string bodyText,
    DateTime createDate,
    int bodyLineOffset)
    : SchemaObject(name, objectId, schemaId, createDate)
{
    public override string ObjectTypeCode => "TR";
    public override string ObjectTypeDescription => "SQL_TRIGGER";

    /// <summary>
    /// The set of DDL event types this trigger fires on, as written:
    /// event-group names (<c>DDL_DATABASE_LEVEL_EVENTS</c>) and individual
    /// events (<c>CREATE_TABLE</c>, <c>DROP_PROCEDURE</c>) alike. Groups
    /// expand to their leaf events for <c>sys.trigger_events</c> and for
    /// <see cref="Covers"/>.
    /// </summary>
    public readonly List<string> EventTypes = eventTypes;

    /// <summary>
    /// Raw source text of the body (everything after <c>AS</c>),
    /// re-tokenized per fire and stored for <c>sys.sql_modules.definition</c>.
    /// </summary>
    public readonly string BodyText = bodyText;

    /// <summary>
    /// Newlines between the <c>CREATE TRIGGER</c> verb and the body's first
    /// token, so a body error reports a line relative to the whole CREATE
    /// statement the way real SQL Server does.
    /// </summary>
    public readonly int BodyLineOffset = bodyLineOffset;

    /// <summary>
    /// True when the trigger is disabled via <c>DISABLE TRIGGER … ON
    /// DATABASE</c>. Disabled triggers stay in the catalog
    /// (<c>sys.triggers.is_disabled</c>) but don't fire.
    /// </summary>
    public bool IsDisabled;

    /// <summary>
    /// The leaf event names <see cref="EventTypes"/> resolves to, groups
    /// expanded through their transitive closure. Built once on first fire —
    /// the declaration never changes after construction (ALTER replaces the
    /// whole instance).
    /// </summary>
    private FrozenSet<string>? coveredEvents;

    /// <summary>
    /// Whether a raised event type dispatches to this trigger. Matching is on
    /// the expanded leaf set, so a trigger declared <c>FOR DDL_TABLE_EVENTS</c>
    /// fires on <c>CREATE_TABLE</c> / <c>ALTER_TABLE</c> / <c>DROP_TABLE</c>
    /// (probe-confirmed — the same three rows it projects into
    /// <c>sys.trigger_events</c>).
    /// </summary>
    public bool Covers(string eventTypeName) =>
        (this.coveredEvents ??= BuildCoveredEvents(this.EventTypes)).Contains(eventTypeName);

    private static FrozenSet<string> BuildCoveredEvents(List<string> declared)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaredName in declared)
        {
            if (!TriggerEventTypes.TryResolve(declaredName, out var entry))
                continue;
            if (TriggerEventTypes.IsGroup(entry))
            {
                foreach (var leaf in TriggerEventTypes.LeafClosure(entry.Type))
                    _ = names.Add(leaf.TypeName);
            }
            else
            {
                _ = names.Add(entry.TypeName);
            }
        }
        return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
