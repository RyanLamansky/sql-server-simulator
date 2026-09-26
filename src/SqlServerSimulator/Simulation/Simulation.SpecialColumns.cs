using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly SqlType[] SpSpecialColumnsSchema =
        [SqlType.SmallInt, SqlType.SystemName, SqlType.SmallInt, SqlType.SystemName, SqlType.Int32, SqlType.Int32, SqlType.SmallInt, SqlType.SmallInt];

    private static readonly string[] SpSpecialColumnsColumnNames =
        ["SCOPE", "COLUMN_NAME", "DATA_TYPE", "TYPE_NAME", "PRECISION", "LENGTH", "SCALE", "PSEUDO_COLUMN"];

    /// <summary>
    /// Handles <c>EXEC sp_special_columns[_100] @table_name [, @table_owner]
    /// [, @table_qualifier] [, @col_type] [, @scope] [, @nullable]
    /// [, @ODBCVer]</c> — ODBC's <c>SQLSpecialColumns</c>, following real's
    /// own procedure (probed 2026-09-26 against SQL Server 2025).
    /// <c>@col_type = 'V'</c> lists the rowversion columns; <c>'R'</c> lists
    /// the key columns of the lowest-numbered unique index, typed as
    /// <c>sp_columns</c> types them under <c>@ODBCVer</c> (default 2).
    /// <c>@nullable = 'O'</c> skips unique indexes with a nullable key column,
    /// and there real's <c>MIN</c> assignment runs once per surviving index
    /// so the variable keeps the last one: the highest-numbered wins.
    /// The classic procedure codes the types a pre-2008 driver can't read as
    /// <c>sp_columns</c> does, without renaming them, and lists the index's
    /// included columns too, since it joins <c>sys.index_columns</c>.
    /// A table that doesn't resolve answers an empty set.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSpecialColumns(BatchContext batch, string procedureName)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        RequireFirstParameter(arguments, procedureName, "table_name");
        var classic = procedureName.Length == "sp_special_columns".Length;
        string? name = null, owner = null, qualifier = null;
        string? colType = "R", scope = "T", nullable = "U";
        var odbcVer = 2;
        var positional = 0;
        foreach (var arg in arguments)
        {
            var slot = arg.Name switch
            {
                null => positional++,
                var n when BuiltInToken.Equals(n, "table_name") => 0,
                var n when BuiltInToken.Equals(n, "table_owner") => 1,
                var n when BuiltInToken.Equals(n, "table_qualifier") => 2,
                var n when BuiltInToken.Equals(n, "col_type") => 3,
                var n when BuiltInToken.Equals(n, "scope") => 4,
                var n when BuiltInToken.Equals(n, "nullable") => 5,
                var n when BuiltInToken.Equals(n, "ODBCVer") => 6,
                _ => throw SimulatedSqlException.InvalidProcedureParameters(procedureName),
            };
            switch (slot)
            {
                case 0: name = CatalogStringArg(arg); break;
                case 1: owner = CatalogStringArg(arg); break;
                case 2: qualifier = CatalogStringArg(arg); break;
                case 3: colType = arg.IsDefault ? "R" : CatalogChar1Arg(arg); break;
                case 4: scope = arg.IsDefault ? "T" : CatalogChar1Arg(arg); break;
                case 5: nullable = arg.IsDefault ? "U" : CatalogChar1Arg(arg); break;
                case 6: odbcVer = CatalogOdbcVer(arg); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters(procedureName);
            }
        }

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        if (colType is null || !(collation.Equals(colType, "R") || collation.Equals(colType, "V")))
            throw RaisedAt(SimulatedSqlException.InvalidProcedureOption("col_type", "'R' or 'V'"), procedureName, 21);
        var scopeOut = scope is not null && collation.Equals(scope, "C") ? (short)0
            : scope is not null && collation.Equals(scope, "T") ? (short)1
            : throw RaisedAt(SimulatedSqlException.InvalidProcedureOption("scope", "'C' or 'T'"), procedureName, 31);
        if (nullable is null || !(collation.Equals(nullable, "U") || collation.Equals(nullable, "O")))
            throw RaisedAt(SimulatedSqlException.InvalidProcedureOption("nullable", "'U' or 'O'"), procedureName, 37);
        if (qualifier is not null && !collation.Equals(qualifier, database.Name))
            throw RaisedAt(SimulatedSqlException.HelpObjectNotInCurrentDatabase(), procedureName, 45);

        var rows = new List<SqlValue[]>();
        // An empty owner quotes to [] alone, which names nothing.
        if (name is not null && owner is not "" && database.Schemas.TryGetValue(owner ?? Database.DefaultSchemaName, out var schema))
        {
            var byName = (odbcVer >= 3 ? SpDatatypeInfoByNameV3 : SpDatatypeInfoByNameV2).Value;
            var rowVersionOnly = collation.Equals(colType, "V");
            if (schema.HeapTables.TryGetValue(name, out var table) && !table.IsTableVariable)
            {
                if (rowVersionOnly)
                {
                    AppendRowVersionRows(rows, table.Columns);
                }
                else if (BestUniqueIndexColumns(table, collation.Equals(nullable, "O"), classic) is { } columns)
                {
                    foreach (var column in columns)
                    {
                        var row = SpecialColumnRow(column, scopeOut, byName, classic);
                        rows.Add(row);
                        // SQL Server 2025's ODBC 3 datatype table carries vector
                        // under varbinary's type id, so real's join lists every
                        // varbinary column twice, the second time as vector.
                        if (odbcVer >= 3 && column.Type is VarbinarySqlType)
                        {
                            SqlValue[] vector = [.. row];
                            if (row[2].AsInt16 != -4)
                                vector[2] = SqlValue.FromInt16(-156);
                            if (column.AliasType is null)
                                vector[3] = SqlValue.FromSystemName("vector");
                            rows.Add(vector);
                        }
                    }
                }
            }
            else if (rowVersionOnly && schema.Views.TryGetValue(name, out var view))
            {
                AppendRowVersionRows(rows, view.OutputColumns);
            }
        }

        yield return new SimulatedSqlResultSet(SpSpecialColumnsSchema, SpSpecialColumnsColumnNames, rows);
    }

    // Real's procedure raises these itself, so they carry its name and the
    // line of its source that raises them.
    private static SimulatedSqlException RaisedAt(SimulatedSqlException exception, string procedureName, int line)
    {
        exception.PreserveDiagnostics(line, procedureName);
        return exception;
    }

    private static string? CatalogChar1Arg(ProcArgument arg) =>
        CatalogStringArg(arg) is { Length: > 1 } text ? text[..1] : CatalogStringArg(arg);

    private static void AppendRowVersionRows(List<SqlValue[]> rows, HeapColumn[] columns)
    {
        foreach (var column in columns)
        {
            if (column.Type != SqlType.RowVersion)
                continue;
            rows.Add([
                SqlValue.Null(SqlType.SmallInt), SqlValue.FromSystemName(column.Name), SqlValue.FromInt16(-2),
                SqlValue.FromSystemName("timestamp"), SqlValue.FromInt32(8), SqlValue.FromInt32(8),
                SqlValue.Null(SqlType.SmallInt), SqlValue.FromInt16(1),
            ]);
        }
    }

    // The unique index real's procedure settles on, as the columns it lists:
    // key columns in key order, then (classic only) the included ones.
    private static List<HeapColumn>? BestUniqueIndexColumns(HeapTable table, bool nonNullableOnly, bool classic)
    {
        List<HeapColumn>? best = null;
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.IsHeap)
                continue;
            List<HeapColumn> keys = [];
            List<HeapColumn> included = [];
            if (identity.Constraint is { } constraint)
            {
                foreach (var ordinal in constraint.StorageOrdinals)
                    keys.Add(table.StoredColumns[ordinal]);
            }
            else if (identity.Index is { IsUnique: true } index)
            {
                foreach (var key in index.KeyColumns)
                    keys.Add(table.Columns[key.ColumnOrdinal]);
                foreach (var ordinal in index.IncludedColumnOrdinals)
                    included.Add(table.Columns[ordinal]);
            }
            else
            {
                continue;
            }

            if (nonNullableOnly && keys.Exists(column => column.Nullable))
                continue;
            if (classic)
                keys.AddRange(included);
            best = keys;
            if (!nonNullableOnly)
                break;
        }

        return best;
    }

    private static SqlValue[] SpecialColumnRow(HeapColumn column, short scope, FrozenDictionary<string, object?[]> byName, bool classic)
    {
        var info = BuildSpColumnsRow(SqlValue.Null(SqlType.SystemName), SqlValue.Null(SqlType.SystemName), SqlValue.Null(SqlType.SystemName), column, 1, byName);
        var dataType = info[4];
        var length = info[7];
        var scale = info[8];
        var precision = info[6];
        if (classic)
        {
            var isMax = column.MaxLength == SqlType.MaxLengthSentinel;
            short? downlevel = column.Type switch
            {
                DateSqlType or TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType => -9,
                XmlSqlType => -10,
                SpatialSqlType or HierarchyIdSqlType => -4,
                NVarcharSqlType when isMax || column.Type is NVarcharSqlType { length: SqlType.MaxLengthSentinel } => -10,
                VarcharSqlType when isMax || column.Type is VarcharSqlType { length: SqlType.MaxLengthSentinel } => -1,
                VarbinarySqlType when isMax || column.Type is VarbinarySqlType { length: SqlType.MaxLengthSentinel } => -4,
                _ => null,
            };
            if (downlevel is { } code)
                dataType = SqlValue.FromInt16(code);
            // An included max-length column reads its max_length, -1.
            if (downlevel is -10 or -1 || (downlevel is -4 && isMax))
                length = SqlValue.FromInt32(-1);
            // A CLR type reads as image to a pre-2008 driver, a large one in
            // precision too.
            if (column.Type is SpatialSqlType or HierarchyIdSqlType)
                length = SqlValue.FromInt32(int.MaxValue);
            if (column.Type is SpatialSqlType)
                precision = SqlValue.FromInt32(int.MaxValue);
            if (downlevel is -9)
            {
                length = SqlValue.FromInt32(info[6].AsInt32 * 2);
                scale = SqlValue.Null(SqlType.SmallInt);
            }
        }

        return [SqlValue.FromInt16(scope), info[3], dataType, info[5], precision, length, scale, SqlValue.FromInt16(1)];
    }
}
