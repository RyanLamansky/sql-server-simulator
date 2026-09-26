using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class BuiltInResources
{
    /// <summary>
    /// The tables a catalog view lists under <paramref name="schema"/>: its
    /// own, and for <c>tempdb</c>'s <c>dbo</c> also the session's <c>#temp</c>
    /// tables and every <c>##</c> table, which real lists there — so
    /// <c>tempdb.sys.columns WHERE object_id = OBJECT_ID('tempdb..#t')</c>
    /// finds a temp table's columns (probed 2026-09-24 against SQL Server
    /// 2025). A <c>#temp</c> keeps its written name here, where real pads it
    /// with underscores to 128 characters around a per-table suffix, and
    /// another session's <c>#temp</c> tables, which real lists too, don't
    /// appear. Without a <paramref name="batch"/> — space accounting, which
    /// has no session — only the schema's own tables are listed.
    /// </summary>
    internal static IEnumerable<HeapTable> CatalogTables(Schema schema, BatchContext? batch) =>
        batch is not null && schema.Name == Database.DefaultSchemaName && schema.Database.Name == Simulation.TempdbDatabaseName
            ? schema.HeapTables.Values
                .Concat(batch.Connection.TempTables.Values)
                .Concat(batch.Connection.Simulation.GlobalTempTables.Values)
            : schema.HeapTables.Values;

    /// <summary>
    /// The tables whose constraints and indexes a catalog view lists under
    /// <paramref name="schema"/>: <see cref="CatalogTables"/>, then every
    /// multi-statement TVF's return table, which real lists the same way under
    /// the function's id (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    internal static IEnumerable<HeapTable> ConstraintHosts(Schema schema, BatchContext? batch) =>
        CatalogTables(schema, batch).Concat(schema.Functions.Values
            .OfType<MultiStatementTableValuedFunction>()
            .OrderBy(f => f.ObjectId)
            .Select(f => f.CatalogShape()));

    /// <summary>
    /// The objects whose declared columns the column-family catalog views
    /// (<c>sys.identity_columns</c> / <c>sys.computed_columns</c> /
    /// <c>sys.default_constraints</c>) report: <see cref="CatalogTables"/>, and
    /// every multi-statement TVF's return table, which real lists the same way
    /// (probed 2026-09-26 against SQL Server 2025). A table's column id is its
    /// stable one; a return table's columns can't be dropped, so theirs is
    /// their position.
    /// </summary>
    private static IEnumerable<(int ObjectId, HeapColumn[] Columns, bool Positional)> DeclaredColumnHosts(Schema schema, BatchContext batch)
    {
        foreach (var table in CatalogTables(schema, batch).OrderBy(t => t.ObjectId))
            yield return (table.ObjectId, table.Columns, false);
        foreach (var function in schema.Functions.Values.OfType<MultiStatementTableValuedFunction>().OrderBy(f => f.ObjectId))
            yield return (function.ObjectId, function.OutputColumns, true);
    }
}
