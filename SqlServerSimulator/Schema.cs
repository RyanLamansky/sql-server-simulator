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
}
