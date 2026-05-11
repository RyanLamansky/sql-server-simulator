using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

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
internal sealed class CatalogView(string name, HeapColumn[] columns, Func<BatchContext, IEnumerable<SqlValue[]>> rowGenerator)
{
    public readonly string Name = name;

    public readonly HeapColumn[] Columns = columns;

    public readonly Func<BatchContext, IEnumerable<SqlValue[]>> RowGenerator = rowGenerator;
}
