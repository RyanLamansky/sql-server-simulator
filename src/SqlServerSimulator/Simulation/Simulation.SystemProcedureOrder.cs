using System.Collections.Frozen;

namespace SqlServerSimulator;

// The ORDER token a system procedure's result sets carry: real's procedures
// end their queries with an ORDER BY, which the TDS endpoint sends as the
// column list after COLMETADATA. A procedure whose order depends on its
// arguments sets it where it builds the result; the rest are listed here.
partial class Simulation
{
    /// <summary>
    /// Per system procedure whose results are ordered the same whatever its
    /// arguments, the ORDER token's column list of each result set it sends,
    /// in order — a null entry for one real sends none with — as captured
    /// from SQL Server 2025 (2026-10-17) with a raw TDS client.
    /// </summary>
    private static readonly FrozenDictionary<string, ushort[]?[]> SystemProcedureResultOrder = new Dictionary<string, ushort[]?[]>(StringComparer.Ordinal)
    {
        ["sp_catalogs"] = [[1]],
        ["sp_check_constraints_rowset"] = [[1, 2, 3]],
        ["sp_column_privileges"] = [[4, 7]],
        ["sp_column_privileges_rowset"] = [[4, 5, 6, 9, 1, 2]],
        ["sp_columns_rowset"] = [[1, 2, 3, 7]],
        ["sp_databases"] = [[1]],
        ["sp_datatype_info"] = [[2, 0, 12, 11, 20]],
        ["sp_datatype_info_90"] = [[2, 0, 12, 11, 20]],
        ["sp_datatype_info_100"] = [[2, 12, 11, 20]],
        ["sp_datatype_info_170"] = [[2, 12, 11, 20]],
        ["sp_dbfixedrolepermission"] = [[1, 2]],
        ["sp_foreign_keys_rowset"] = [[8, 9, 2, 3, 13]],
        ["sp_help_fulltext_catalogs"] = [[1]],
        ["sp_helpdb"] = [[1], [2]],
        ["sp_helpextendedproc"] = [[1]],
        ["sp_helplogins"] = [[1], [1, 2, 3]],
        ["sp_helprolemember"] = [[1, 2]],
        ["sp_helprotect"] = [[0, 1, 2, 3, 4, 5, 6, 0]],
        ["sp_helpserver"] = [[1]],
        ["sp_helpstats"] = [[1]],
        ["sp_helptext"] = [[0]],
        ["sp_helpuser"] = [[1]],
        ["sp_indexes_rowset"] = [[8, 10, 4, 5, 6, 17]],
        ["sp_linkedservers"] = [[1]],
        ["sp_pkeys"] = [[1, 2, 3, 5]],
        ["sp_primary_keys_rowset"] = [[2, 3]],
        ["sp_procedure_params_rowset"] = [[2, 3, 5]],
        ["sp_procedures_rowset"] = [[2, 3]],
        ["sp_provider_types_rowset"] = [[2, 20, 19, 8]],
        ["sp_schemata_rowset"] = [[2]],
        ["sp_sproc_columns"] = [[1, 2, 3, 18]],
        ["sp_srvrolepermission"] = [[1, 2]],
        ["sp_statistics"] = [[4, 7, 6, 8]],
        ["sp_statistics_100"] = [[4, 7, 6, 8]],
        ["sp_statistics_rowset"] = [[2, 3]],
        ["sp_table_constraints_rowset"] = [[2, 3, 5, 6, 7]],
        ["sp_table_privileges"] = [[2, 3, 6, 5]],
        ["sp_table_privileges_rowset"] = [[4, 5, 6, 1, 2]],
        ["sp_table_types"] = [[1, 2, 3]],
        ["sp_tables"] = [[4, 1, 2, 3]],
        ["sp_tables_ex"] = [[4, 1, 2, 3]],
        ["sp_tables_rowset"] = [[4, 2, 3]],
        ["sp_who2"] = [[0]],
        ["sp_column_privileges_ex"] = [[1, 2, 3, 4, 7]],
        ["sp_columns_ex"] = [[1, 2, 3, 17]],
        ["sp_foreignkeys"] = [[5, 6, 7, 9]],
        ["sp_indexes"] = [[4, 7, 5, 6, 8]],
        ["sp_primarykeys"] = [[1, 2, 3, 5]],
        ["sp_table_privileges_ex"] = [[1, 2, 3, 6]],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary><paramref name="result"/>, carrying <paramref name="columns"/> as its ORDER token's column list.</summary>
    private static SimulatedSqlResultSet WithOrder(SimulatedSqlResultSet result, ushort[] columns)
    {
        result.OrderColumns = columns;
        return result;
    }

    /// <summary>
    /// Passes a system procedure's outcomes through, giving each result set
    /// that carries no ORDER token of its own the one
    /// <see cref="SystemProcedureResultOrder"/> lists for its position.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> WithSystemResultOrder(IEnumerable<SimulatedStatementOutcome> outcomes, string systemProcName)
    {
        if (!SystemProcedureResultOrder.TryGetValue(systemProcName, out var orders))
            return outcomes;
        return Ordered(outcomes, orders);

        static IEnumerable<SimulatedStatementOutcome> Ordered(IEnumerable<SimulatedStatementOutcome> outcomes, ushort[]?[] orders)
        {
            var position = 0;
            foreach (var outcome in outcomes)
            {
                if (outcome is SimulatedQueryResult result)
                {
                    if (position < orders.Length)
                        result.OrderColumns ??= orders[position];
                    position++;
                }
                yield return outcome;
            }
        }
    }
}
