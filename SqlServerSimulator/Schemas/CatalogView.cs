using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A virtual table in the <c>sys</c> schema — backs <c>sys.tables</c>,
/// <c>sys.objects</c>, <c>sys.schemas</c>, etc. Unlike a <see cref="HeapTable"/>,
/// a catalog view has no persistent storage: its rows are projected from
/// live <see cref="Database"/> / <see cref="Schema"/> / <see cref="HeapTable"/>
/// metadata at query time. A fresh row sequence materializes on every
/// SELECT, so changes made earlier in the same batch (CREATE TABLE,
/// CREATE SCHEMA, DROP TABLE, etc.) appear immediately in the next read.
/// </summary>
/// <remarks>
/// <para>
/// Registered process-wide in <see cref="Simulation.CatalogViews"/> and
/// looked up by leaf name when a FROM clause resolves a <c>sys.&lt;view&gt;</c>
/// reference. The simulator only recognizes the leaf names registered here;
/// other <c>sys.*</c> names raise Msg 208 — same as a missing user table.
/// </para>
/// <para>
/// The row generator produces <see cref="SqlValue"/> arrays in column order
/// (matching <see cref="Columns"/>). The FROM-source wiring encodes each
/// row into bytes via <see cref="RowEncoder"/> on iteration; the byte
/// representation is never cached, so the view stays in sync with live
/// metadata without bookkeeping.
/// </para>
/// </remarks>
internal sealed class CatalogView(string name, HeapColumn[] columns, Func<BatchContext, Database, IEnumerable<SqlValue[]>> rowGenerator)
{
    public readonly string Name = name;

    /// <summary>
    /// Deterministic, process-stable <c>object_id</c> surfaced by
    /// <c>OBJECT_ID('sys.&lt;view&gt;')</c>. Catalog views are registered
    /// process-wide (not per-database) so they can't draw from a
    /// <see cref="Database"/>'s object-id allocator; instead the id is a
    /// 32-bit FNV-1a hash of the leaf name forced negative, keeping it stable
    /// across runs and disjoint from the positive ids user objects allocate
    /// (from 100). Load-bearing only for OBJECT_ID resolving to non-NULL —
    /// SSMS's Query Store probe gates on
    /// <c>OBJECT_ID(N'[sys].[database_query_store_options]') IS NOT NULL</c>.
    /// Not byte-identical to real SQL Server's small fixed system-view ids.
    /// </summary>
    public readonly int ObjectId = ComputeObjectId(name);

    private static int ComputeObjectId(string leafName)
    {
        var hash = Simulation.Fnv1a32.Initial;
        hash.Mix(leafName);
        return (int)(hash.Value | 0x8000_0000);
    }

    public readonly HeapColumn[] Columns = columns;

    /// <summary>
    /// Row generator. The <see cref="Database"/> parameter is the database
    /// the view was scoped to — for an unqualified or 2-part reference
    /// (<c>sys.tables</c>) it's the connection's
    /// <see cref="SimulatedDbConnection.CurrentDatabase"/>; for a 3-part
    /// reference (<c>WideWorldImporters.sys.tables</c>) it's whichever
    /// <see cref="Database"/> the qualifier resolved to in
    /// <see cref="Simulation.Databases"/>. Enumerators must read from this
    /// parameter rather than <c>batch.CurrentDatabase</c> so cross-database
    /// catalog inspection lands correctly.
    /// </summary>
    public readonly Func<BatchContext, Database, IEnumerable<SqlValue[]>> RowGenerator = rowGenerator;
}
