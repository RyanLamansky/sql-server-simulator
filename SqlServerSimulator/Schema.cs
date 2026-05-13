using System.Collections.Concurrent;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One namespace inside a <see cref="Database"/>. A <see cref="Schema"/>
/// holds the user tables that resolve under its name; <c>SELECT * FROM
/// audit.t</c> routes through <see cref="Database.Schemas"/>[<c>"audit"</c>]
/// to find <c>t</c>. Every <see cref="Database"/> ships with a
/// <see cref="Database.DefaultSchemaName"/>-named instance; <c>CREATE SCHEMA
/// &lt;name&gt;</c> adds more. Views / procs / sequences will land alongside
/// <see cref="HeapTables"/> here when those features arrive — the type's
/// shape is in place so they can graft on without touching the resolution
/// rule again.
/// </summary>
internal sealed class Schema(string name, int schemaId)
{
    public readonly string Name = name;

    /// <summary>
    /// Per-database schema identifier — surfaces in <c>SCHEMA_ID()</c>,
    /// <c>sys.schemas.schema_id</c>, <c>sys.tables.schema_id</c>, and
    /// <c>sys.objects.schema_id</c>. The built-in schemas use the real-SQL-
    /// Server-conventional values (<c>dbo=1</c>, <c>INFORMATION_SCHEMA=3</c>,
    /// <c>sys=4</c>); user schemas allocate from <see cref="Database.AllocateSchemaId"/>
    /// starting at 5. Apps occasionally hard-code <c>schema_id = 1</c> for
    /// dbo, so matching is worth the small bit of setup logic.
    /// </summary>
    public readonly int SchemaId = schemaId;

    public readonly ConcurrentDictionary<string, HeapTable> HeapTables = new(Collation.Default);

    /// <summary>
    /// User-defined functions hosted by this schema — either scalar
    /// (<see cref="ScalarFunction"/>) or inline TVF
    /// (<see cref="InlineTableValuedFunction"/>) under the
    /// <see cref="UserDefinedFunction"/> base.
    /// </summary>
    public readonly ConcurrentDictionary<string, UserDefinedFunction> Functions = new(Collation.Default);

    /// <summary>
    /// Views hosted by this schema. <c>CREATE VIEW schema.name AS SELECT
    /// ...</c> adds entries here; FROM-clause references resolve through
    /// this dict before falling through to the heap-table lookup. Views
    /// share the same name namespace as tables (Msg 2714 on collision),
    /// so a view's leaf name is unique across both dicts within the schema.
    /// </summary>
    public readonly ConcurrentDictionary<string, View> Views = new(Collation.Default);

    /// <summary>
    /// Stored procedures hosted by this schema. <c>CREATE PROCEDURE
    /// schema.name AS ...</c> adds entries; <c>EXEC schema.name ...</c>
    /// resolves through this dict. Procedures share the object-name
    /// namespace with tables / views / functions (Msg 2714 on collision).
    /// <c>ALTER PROCEDURE</c> replaces the entry while preserving the
    /// existing <see cref="SchemaObject.ObjectId"/>; <c>DROP PROCEDURE</c>
    /// removes it.
    /// </summary>
    public readonly ConcurrentDictionary<string, Procedure> Procedures = new(Collation.Default);

    /// <summary>
    /// User-defined table types hosted by this schema. Created via
    /// <c>CREATE TYPE schema.name AS TABLE (...)</c>, consumed by
    /// <c>DECLARE @t schema.name</c> and as <c>READONLY</c> procedure
    /// parameters (TVPs). Probed against SQL Server 2025: type names occupy
    /// a separate namespace from tables / views / functions / procs — a
    /// table type can share a leaf with a table (Msg 219 on dup type name
    /// only).
    /// </summary>
    public readonly ConcurrentDictionary<string, TableType> TableTypes = new(Collation.Default);

    /// <summary>
    /// Sequence objects hosted by this schema. Created via <c>CREATE
    /// SEQUENCE schema.name ...</c>, consumed via <c>NEXT VALUE FOR
    /// schema.name</c>. Shares the object-name namespace with tables /
    /// views / functions / procs (Msg 2714 on collision).
    /// </summary>
    public readonly ConcurrentDictionary<string, Sequence> Sequences = new(Collation.Default);

    /// <summary>
    /// DML triggers hosted by this schema. Created via <c>CREATE [OR
    /// ALTER] TRIGGER schema.name ON schema.parent { AFTER | INSTEAD OF }
    /// ... AS body</c>; fired automatically by INSERT / UPDATE / DELETE /
    /// MERGE against the trigger's parent (table or view). The trigger
    /// NAME shares the object-name namespace with tables / views /
    /// functions / procs / sequences (Msg 2714 on collision). Lookup at
    /// DML time scans this dict for triggers whose <see cref="Trigger.Parent"/>
    /// matches the DML target — a per-table cache lives on
    /// <see cref="HeapTable"/> itself but the dict here is the source
    /// of truth (ENABLE / DISABLE / DROP all operate on it).
    /// </summary>
    public readonly ConcurrentDictionary<string, Trigger> Triggers = new(Collation.Default);

    /// <summary>
    /// Yields every <see cref="SchemaObject"/> in this schema's
    /// object-name namespace (heap tables, views, UDFs, procedures,
    /// sequences, triggers) — the set whose leaf names must be unique
    /// (Msg 2714 on CREATE collision). <see cref="TableTypes"/> are
    /// deliberately omitted: probe-confirmed that table-type names occupy
    /// a separate namespace from this set. Used by sys.objects projection
    /// and by the CREATE-time auto-name-collision check.
    /// </summary>
    public IEnumerable<SchemaObject> SchemaObjects()
    {
        foreach (var t in this.HeapTables.Values) yield return t;
        foreach (var v in this.Views.Values) yield return v;
        foreach (var fn in this.Functions.Values) yield return fn;
        foreach (var p in this.Procedures.Values) yield return p;
        foreach (var s in this.Sequences.Values) yield return s;
        foreach (var tr in this.Triggers.Values) yield return tr;
    }

    /// <summary>
    /// True when <paramref name="leaf"/> matches any existing name in
    /// this schema's object-name namespace (<see cref="SchemaObjects"/>).
    /// Used by every CREATE path to raise Msg 2714 before allocating an
    /// ObjectId or writing into the relevant dict.
    /// </summary>
    public bool HasNameInSharedNamespace(string leaf)
    {
        foreach (var obj in this.SchemaObjects())
        {
            if (Collation.Default.Equals(obj.Name, leaf))
                return true;
        }
        return false;
    }
}
