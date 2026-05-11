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
internal sealed class Schema(string name)
{
    public readonly string Name = name;

    public readonly ConcurrentDictionary<string, HeapTable> HeapTables = new(Collation.Default);
}
