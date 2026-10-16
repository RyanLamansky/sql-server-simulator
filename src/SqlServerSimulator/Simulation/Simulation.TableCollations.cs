using SqlServerSimulator.Network;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Column schema for <c>sp_tablecollations_100</c>: <c>colid int</c>,
    /// <c>name sysname</c>, <c>tds_collation binary(5)</c> (NULL for
    /// non-string columns), <c>collation sysname</c> (NULL for non-string
    /// columns). Shape probe-confirmed against SQL Server 2025 (2026-07-18)
    /// via <c>sp_describe_first_result_set</c>.
    /// </summary>
    private static readonly SqlType[] TableCollationsSchema =
        [SqlType.Int32, SqlType.SystemName, SqlType.GetBinary(5), SqlType.SystemName];

    private static readonly string[] TableCollationsColumnNames =
        ["colid", "name", "tds_collation", "collation"];

    private static readonly bool[] TableCollationsNullability = [false, true, true, true];

    /// <summary>
    /// Handles <c>EXEC sp_tablecollations_100 N'[schema].[table]'</c> — the
    /// per-column collation metadata query SqlClient's <c>SqlBulkCopy</c> runs
    /// before streaming bulk rows. Returns one row per column of the named
    /// table in <c>column_id</c> order (including identity / computed /
    /// rowversion columns), carrying the column's 1-based ordinal, its name,
    /// its 5-byte TDS collation structure, and the collation name — the latter
    /// two NULL for non-string columns, matching real SQL Server. An
    /// unresolvable table name yields an empty result set (real filters on
    /// <c>object_id = NULL</c>). <c>sp_tablecollations_90</c> gives the date
    /// and time types after <c>datetime</c> the database's collation, as the
    /// strings a down-level client reads them as, and the older
    /// <c>sp_tablecollations</c> <c>xml</c> too (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTableCollations(BatchContext batch, string procedure, SpColumnsVersion version)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        RequireFirstParameter(arguments, procedure, "object");
        var rows = new List<SqlValue[]>();
        if (arguments.Count > 0
            && !arguments[0].Value.IsNull
            && ObjectId.TryParseObjectName(arguments[0].Value.CoerceTo(SqlType.NVarchar).AsString, out var parsed)
            && batch.TryResolveTable(parsed, out var table))
        {
            // A column set stands in for its sparse columns (probed 2026-10-16
            // against SQL Server 2025).
            var hasColumnSet = Array.Exists(table.Columns, static column => column.IsColumnSet);
            for (var i = 0; i < table.Columns.Length; i++)
            {
                var column = table.Columns[i];
                if (hasColumnSet && column.IsSparse)
                    continue;
                var collation = column.Type switch
                {
                    _ when column.IsColumnSet => version == SpColumnsVersion.Classic ? batch.CurrentDatabase.Collation : null,
                    { Category: SqlTypeCategory.String } => column.Type.Collation,
                    DateSqlType or TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType when version != SpColumnsVersion.V100 => batch.CurrentDatabase.Collation,
                    XmlSqlType when version == SpColumnsVersion.Classic => batch.CurrentDatabase.Collation,
                    _ => null,
                };
                SqlValue tdsCollation;
                SqlValue collationName;
                if (collation is null)
                {
                    tdsCollation = SqlValue.Null(TableCollationsSchema[2]);
                    collationName = SqlValue.Null(SqlType.SystemName);
                }
                else
                {
                    var codec = TdsCollationCodec.For(collation);
                    tdsCollation = SqlValue.FromBinary(TableCollationsSchema[2],
                    [
                        (byte)codec.Info,
                        (byte)(codec.Info >> 8),
                        (byte)(codec.Info >> 16),
                        (byte)(codec.Info >> 24),
                        codec.SortId,
                    ]);
                    collationName = SqlValue.FromString(SqlType.SystemName, collation.Name);
                }

                rows.Add([SqlValue.FromInt32(i + 1), SqlValue.FromString(SqlType.SystemName, column.Name), tdsCollation, collationName]);
            }
        }

        yield return new SimulatedSqlResultSet(TableCollationsSchema, TableCollationsColumnNames, rows) { ColumnNullability = TableCollationsNullability };
    }
}
