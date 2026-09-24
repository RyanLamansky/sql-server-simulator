using SqlServerSimulator.Parser;
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
}
