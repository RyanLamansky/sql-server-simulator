using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class BuiltInResources
{
    /// <summary>
    /// The tables a catalog view lists under <paramref name="schema"/>: its
    /// own, and for <c>tempdb</c>'s <c>dbo</c> also <see cref="TempCatalogTables"/>.
    /// Without a <paramref name="batch"/> — space accounting, which has no
    /// session — only the schema's own tables are listed.
    /// </summary>
    internal static IEnumerable<HeapTable> CatalogTables(Schema schema, BatchContext? batch) =>
        schema.HeapTables.EnumerateValues().Concat(TempCatalogTables(schema, batch));

    /// <summary>
    /// What <c>tempdb</c>'s <c>dbo</c> lists beside its own tables, each under
    /// its name inside <c>tempdb</c> (<see cref="HeapTable.CatalogName"/>):
    /// every session's <c>#temp</c> tables, every <c>##</c> table, and the
    /// running batch's table variables — real lists every session's, which
    /// live on batches the reading session can't reach (probed 2026-10-06
    /// against SQL Server 2025). Empty for any other schema.
    /// </summary>
    internal static List<HeapTable> TempCatalogTables(Schema schema, BatchContext? batch)
    {
        var tables = new List<HeapTable>();
        if (batch is null || schema.Name != Database.DefaultSchemaName || schema.Database.Name != Simulation.TempdbDatabaseName)
            return tables;
        var simulation = batch.Connection.Simulation;
        SessionToken[] sessions;
        lock (simulation.Sessions)
            sessions = [.. simulation.Sessions];
        foreach (var session in sessions)
        {
            if (session.Owner is { } owner && owner.TryGetTarget(out var connection))
                tables.AddRange(connection.TempTables.EnumerateValues());
        }
        tables.AddRange(simulation.GlobalTempTables.EnumerateValues());
        foreach (var (_, variable) in batch.TableVariables ?? [])
        {
            if (variable.InternalName is not null)
                tables.Add(variable);
        }
        return tables;
    }

    /// <summary>
    /// The tables whose constraints and indexes a catalog view lists under
    /// <paramref name="schema"/>: <see cref="CatalogTables"/>, then every
    /// multi-statement TVF's return table, which real lists the same way under
    /// the function's id, then every table type's backing type table, which
    /// real lists under the <c>sys</c> schema as shipped
    /// (<see cref="HeapTable.IsTypeTable"/>; probed 2026-09-26 against SQL
    /// Server 2025).
    /// </summary>
    internal static IEnumerable<HeapTable> ConstraintHosts(Schema schema, BatchContext? batch) =>
        CatalogTables(schema, batch)
            .Concat(schema.Functions.EnumerateValues()
                .OfType<MultiStatementTableValuedFunction>()
                .OrderBy(f => f.ObjectId)
                .Select(f => f.CatalogShape()))
            .Concat(schema.TableTypes.EnumerateValues().OrderBy(t => t.ObjectId).Select(t => t.CatalogShape));

    /// <summary>
    /// The objects whose declared columns the column-family catalog views
    /// (<c>sys.identity_columns</c> / <c>sys.computed_columns</c> /
    /// <c>sys.default_constraints</c>) report: <see cref="CatalogTables"/>, and
    /// every multi-statement TVF's return table, which real lists the same way
    /// (probed 2026-09-26 against SQL Server 2025), then every table type's
    /// backing type table, reported under the <c>sys</c> schema as shipped. A
    /// table's column id is its stable one; a return table's or type table's
    /// columns can't be dropped, so theirs is their position.
    /// </summary>
    private static IEnumerable<(int ObjectId, HeapColumn[] Columns, bool Positional, bool IsTypeTable)> DeclaredColumnHosts(Schema schema, BatchContext batch)
    {
        foreach (var table in CatalogTables(schema, batch).OrderBy(t => t.ObjectId))
            yield return (table.ObjectId, table.Columns, false, false);
        foreach (var function in schema.Functions.EnumerateValues().OfType<MultiStatementTableValuedFunction>().OrderBy(f => f.ObjectId))
            yield return (function.ObjectId, function.OutputColumns, true, false);
        foreach (var tableType in schema.TableTypes.EnumerateValues().OrderBy(t => t.ObjectId))
            yield return (tableType.ObjectId, tableType.Columns, true, true);
    }
}
