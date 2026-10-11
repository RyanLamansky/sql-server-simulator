using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The OLE DB schema-rowset procedures over a table's indexes, keys,
// constraints, statistics and privileges, and the database list.
partial class Simulation
{
    /// <summary>
    /// <c>sp_indexes_rowset</c> and its versions: the OLE DB <c>INDEXES</c>
    /// rowset, one row per key column of each rowstore index of the tables
    /// named — a heap's row aside — sorted unique ones last, then by index
    /// name (probed 2026-10-11 against SQL Server 2025). A unique index's
    /// <c>CARDINALITY</c> is the table's row count; <c>_90</c> and
    /// <c>_100</c> add <c>STATUS</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeIndexesRowset(BatchContext batch, string procedure, string calledAs, bool withStatus)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("index_name"), RowsetNameParameter("table_schema")]
            : [RowsetNameParameter("table_name", required: true), RowsetNameParameter("index_name"), RowsetNameParameter("table_schema")];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var offset = everyObject ? -1 : 0;
        var name = everyObject ? null : RowsetText(values[0]);
        var indexName = RowsetText(values[1 + offset]);
        var schema = RowsetText(values[2 + offset]);

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        var rows = new List<(bool Unique, string Index, int Ordinal, SqlValue[] Row)>();
        if (everyObject || name is not null)
        {
            foreach (var item in RowsetObjects(batch, name, schema))
            {
                if (item.Table is not { } table)
                    continue;
                var liveRows = (int)table.Heap.CountLiveRows();
                var pages = liveRows == 0 ? 0 : table.Heap.Pages.Count;
                foreach (var identity in table.IndexIdentities())
                {
                    if (identity.Type is not (1 or 2) || (indexName is not null && !collation.Equals(indexName, identity.Name!)))
                        continue;
                    var (nonUnique, keyColumns) = StatisticsKeyColumns(table, identity);
                    var unique = nonUnique == 0;
                    var clustered = identity.Type == 1;
                    var (fillFactor, noRecompute, filter) = identity.Constraint is { } key
                        ? (key.FillFactor, key.StatisticsNoRecompute, null)
                        : (identity.Index!.FillFactor, identity.Index.StatisticsNoRecompute, identity.Index.FilterDefinition);
                    for (var i = 0; i < keyColumns.Length; i++)
                    {
                        var row = new SqlValue[withStatus ? 26 : 25];
                        row[0] = catalog;
                        row[1] = SqlValue.FromNVarchar(RowsetName, item.Schema);
                        row[2] = SqlValue.FromNVarchar(RowsetName, item.Name);
                        row[3] = catalog;
                        row[4] = row[1];
                        row[5] = SqlValue.FromNVarchar(RowsetName, identity.Name!);
                        row[6] = SqlValue.FromBoolean(identity.Constraint is { Kind: KeyConstraintKind.PrimaryKey });
                        row[7] = SqlValue.FromBoolean(unique);
                        row[8] = SqlValue.FromBoolean(clustered);
                        row[9] = SqlValue.FromInt16(1);
                        row[10] = SqlValue.FromInt32(fillFactor);
                        row[11] = SqlValue.Null(SqlType.Int32);
                        row[12] = SqlValue.Null(SqlType.Int32);
                        row[13] = SqlValue.FromBoolean(false);
                        row[14] = SqlValue.FromBoolean(!noRecompute);
                        row[15] = SqlValue.FromInt32(4);
                        row[16] = SqlValue.FromInt32(i + 1);
                        row[17] = SqlValue.FromNVarchar(RowsetName, keyColumns[i].Name);
                        row[18] = SqlValue.Null(SqlType.UniqueIdentifier);
                        row[19] = SqlValue.Null(SqlType.Int32);
                        row[20] = SqlValue.FromInt16(keyColumns[i].Descending ? (short)2 : (short)1);
                        row[21] = unique ? SqlValue.FromInt32(liveRows) : SqlValue.Null(SqlType.Int32);
                        row[22] = SqlValue.FromInt32(pages);
                        row[23] = filter is null ? SqlValue.Null(RowsetFilter) : SqlValue.FromNVarchar(RowsetFilter, filter);
                        row[24] = SqlValue.FromBoolean(clustered);
                        if (withStatus)
                            row[25] = SqlValue.FromInt32(0);
                        rows.Add((unique, identity.Name!, i, row));
                    }
                }
            }
        }
        rows.Sort((a, b) =>
        {
            var byUnique = a.Unique.CompareTo(b.Unique);
            if (byUnique != 0)
                return byUnique;
            var bySchema = collation.Compare(a.Row[4].AsString, b.Row[4].AsString);
            if (bySchema != 0)
                return bySchema;
            var byName = collation.Compare(a.Index, b.Index);
            return byName != 0 ? byName : a.Ordinal.CompareTo(b.Ordinal);
        });
        var count = withStatus ? 26 : 25;
        yield return new SimulatedSqlResultSet(RowsetIndexesSchema[..count], RowsetIndexesNames[..count], [.. rows.Select(static r => r.Row)]);
    }

    /// <summary>
    /// <c>sp_check_constbytable_rowset</c> / <c>…2</c>: the OLE DB
    /// <c>CHECK_CONSTRAINTS_BY_TABLE</c> rowset, each CHECK constraint of the
    /// tables named with its definition.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeCheckConstraintsRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("table_schema"), RowsetNameParameter("constraint_name"), RowsetNameParameter("constraint_schema")]
            : [RowsetNameParameter("table_name", required: true), RowsetNameParameter("table_schema"), RowsetNameParameter("constraint_name"), RowsetNameParameter("constraint_schema")];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var offset = everyObject ? -1 : 0;
        var name = everyObject ? null : RowsetText(values[0]);
        var schema = RowsetText(values[1 + offset]);
        var constraintName = RowsetText(values[2 + offset]);
        var constraintSchema = RowsetText(values[3 + offset]);

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        var rows = new List<SqlValue[]>();
        if (everyObject || name is not null)
        {
            foreach (var item in RowsetObjects(batch, name, schema))
            {
                if (item.Table is not { } table || (constraintSchema is not null && !collation.Equals(constraintSchema, item.Schema)))
                    continue;
                var constraints = new List<CheckConstraint>(table.CheckConstraints);
                constraints.Sort((a, b) => collation.Compare(a.Name, b.Name));
                foreach (var check in constraints)
                {
                    if (constraintName is not null && !collation.Equals(constraintName, check.Name))
                        continue;
                    var schemaValue = SqlValue.FromNVarchar(RowsetName, item.Schema);
                    rows.Add([
                        catalog, schemaValue, SqlValue.FromNVarchar(RowsetName, item.Name), catalog, schemaValue, SqlValue.FromNVarchar(RowsetName, check.Name),
                        check.Definition is null ? SqlValue.Null(RowsetText4000) : SqlValue.FromNVarchar(RowsetText4000, check.Definition),
                        SqlValue.Null(RowsetDescription),
                    ]);
                }
            }
        }
        yield return new SimulatedSqlResultSet(RowsetCheckConstraintsSchema, RowsetCheckConstraintsNames, rows);
    }

    /// <summary>
    /// <c>sp_table_statistics2_rowset</c>: the OLE DB <c>TABLE_STATISTICS</c>
    /// rowset, which real reads out of <c>DBCC SHOW_STATISTICS</c> — so it
    /// sends one result set per statistic of the table, by name, each
    /// followed by DBCC's completion message, with a row per density-vector
    /// prefix naming its last column; a statistic never built sends its
    /// result set empty, and a view, a table without statistics or another
    /// catalog one empty result set. A name nothing has is Msg 20001 (probed
    /// 2026-10-11 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeTableStatisticsRowset(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        SystemProcedureParameter[] parameters =
        [
            RowsetNameParameter("table_name", required: true), RowsetNameParameter("table_schema"), RowsetNameParameter("table_catalog"),
            RowsetNameParameter("stat_name"), RowsetNameParameter("stat_schema"), RowsetNameParameter("stat_catalog"),
        ];
        var values = BindSystemProcedureArguments("sp_table_statistics2_rowset", calledAs, arguments, parameters);
        var name = RowsetText(values[0]);
        var schema = RowsetText(values[1]);
        var tableCatalog = RowsetText(values[2]);
        var statName = RowsetText(values[3]);
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        if (tableCatalog is not null && !collation.Equals(tableCatalog, database.Name))
        {
            yield return new SimulatedSqlResultSet(RowsetStatisticsSchema, RowsetStatisticsNames, []);
            yield break;
        }

        var objects = name is null ? [] : RowsetObjects(batch, name, schema);
        if (objects.Count == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NoNicknameForArticle(), 19);

        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        var sent = false;
        foreach (var item in objects)
        {
            if (item.Table is not { } table)
                continue;
            var statistics = StatisticsOn(table);
            statistics.Sort((a, b) => collation.Compare(a.Name, b.Name));
            foreach (var statistic in statistics)
            {
                if (statName is not null && !collation.Equals(statName, statistic.Name))
                    continue;
                var schemaValue = SqlValue.FromNVarchar(RowsetName, item.Schema);
                var rows = new List<SqlValue[]>();
                if (statistic.State.Snapshot is { } snapshot)
                {
                    float previousLength = 0;
                    for (var i = 0; i < snapshot.DensityVector.Length && i < statistic.DensityOrdinals.Length; i++)
                    {
                        var entry = snapshot.DensityVector[i];
                        rows.Add([
                            catalog, schemaValue, SqlValue.FromNVarchar(RowsetName, item.Name), catalog, schemaValue,
                            SqlValue.FromNVarchar(RowsetName, statistic.Name), SqlValue.FromInt16(5),
                            SqlValue.FromNVarchar(RowsetName, table.Columns[statistic.DensityOrdinals[i]].Name),
                            SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(SqlType.Int32), SqlValue.FromInt32(i + 1),
                            SqlValue.FromInt16(100), SqlValue.FromDateTime(snapshot.Updated), SqlValue.FromInt32(snapshot.Histogram.Length),
                            SqlValue.Null(SqlType.BigInt),
                            SqlValue.FromInt64(entry.AllDensity > 0 ? (long)Math.Round(1 / entry.AllDensity) : 0),
                            SqlValue.FromInt64(snapshot.Rows), SqlValue.FromSingle(entry.AverageLength - previousLength),
                        ]);
                        previousLength = entry.AverageLength;
                    }
                }
                yield return new SimulatedSqlResultSet(RowsetStatisticsSchema, RowsetStatisticsNames, rows);
                var completed = SimulatedSqlException.DbccExecutionCompletedMessage(batch);
                completed.Procedure = calledAs;
                completed.LineNumber = 105;
                yield return new SimulatedInfoOutcome(completed);
                sent = true;
            }
        }
        if (!sent)
            yield return new SimulatedSqlResultSet(RowsetStatisticsSchema, RowsetStatisticsNames, []);
    }

    /// <summary>
    /// <c>sp_table_statistics_rowset</c>, the provider generation before
    /// statistics: its one result set, whose <c>AVG_COLUMN_LENGTH</c> is an
    /// <c>int</c>, is always empty.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeEmptyTableStatisticsRowset(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        _ = BindSystemProcedureArguments("sp_table_statistics_rowset", calledAs, arguments, [RowsetNameParameter("table_name_dummy")]);
        yield return new SimulatedSqlResultSet([.. RowsetStatisticsSchema[..^1], SqlType.Int32], RowsetStatisticsNames, []);
    }

    /// <summary>
    /// <c>sp_primary_keys_rowset</c> / <c>…2</c>: the OLE DB
    /// <c>PRIMARY_KEYS</c> rowset, the primary key's columns in key order.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokePrimaryKeysRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("table_schema")]
            : [RowsetNameParameter("table_name", required: true), RowsetNameParameter("table_schema")];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var name = everyObject ? null : RowsetText(values[0]);
        var schema = RowsetText(values[everyObject ? 0 : 1]);

        var catalog = SqlValue.FromNVarchar(RowsetName, batch.CurrentDatabase.Name);
        var rows = new List<SqlValue[]>();
        if (everyObject || name is not null)
        {
            foreach (var item in RowsetObjects(batch, name, schema))
            {
                if (item.Table is not { } table || FindPrimaryKey(table) is not { } key)
                    continue;
                for (var i = 0; i < key.StorageOrdinals.Length; i++)
                {
                    rows.Add([
                        catalog, SqlValue.FromNVarchar(RowsetName, item.Schema), SqlValue.FromNVarchar(RowsetName, item.Name),
                        SqlValue.FromNVarchar(RowsetName, table.StoredColumns[key.StorageOrdinals[i]].Name), SqlValue.Null(SqlType.UniqueIdentifier),
                        SqlValue.Null(SqlType.Int32), SqlValue.FromInt32(i + 1), SqlValue.FromNVarchar(RowsetName, key.Name),
                    ]);
                }
            }
        }
        yield return new SimulatedSqlResultSet(RowsetPrimaryKeysSchema, RowsetPrimaryKeysNames, rows);
    }

    /// <summary>
    /// <c>sp_foreign_keys_rowset</c> and its versions: the OLE DB
    /// <c>FOREIGN_KEYS</c> rowset. The plain form starts from the referenced
    /// table, which it requires — a NULL one lists nothing — and the "2" form
    /// from the referencing one; the "3" form lists every key of the
    /// schemas named. The rules read as <c>NO ACTION</c>, <c>CASCADE</c>,
    /// <c>SET NULL</c> or <c>SET DEFAULT</c>, and <c>DEFERRABILITY</c> is 3.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeForeignKeysRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string? pkName = null, pkSchema = null, pkCatalog = null, fkName = null, fkSchema = null, fkCatalog = null;
        switch (procedure)
        {
            case "sp_foreign_keys_rowset":
                {
                    var values = BindSystemProcedureArguments(procedure, calledAs, arguments,
                    [
                        RowsetNameParameter("pk_table_name", required: true), RowsetNameParameter("pk_table_schema"), RowsetNameParameter("foreignkey_tab_name"),
                        RowsetNameParameter("foreignkey_tab_schema"), RowsetNameParameter("foreignkey_tab_catalog"),
                    ]);
                    (pkName, pkSchema, fkName, fkSchema, fkCatalog) = (RowsetText(values[0]), RowsetText(values[1]), RowsetText(values[2]), RowsetText(values[3]), RowsetText(values[4]));
                    pkName ??= "";
                    break;
                }
            case "sp_foreign_keys_rowset2":
                {
                    var values = BindSystemProcedureArguments(procedure, calledAs, arguments,
                    [
                        RowsetNameParameter("foreignkey_tab_name", required: true), RowsetNameParameter("foreignkey_tab_schema"), RowsetNameParameter("pk_table_name"),
                        RowsetNameParameter("pk_table_schema"), RowsetNameParameter("pk_table_catalog"),
                    ]);
                    (fkName, fkSchema, pkName, pkSchema, pkCatalog) = (RowsetText(values[0]), RowsetText(values[1]), RowsetText(values[2]), RowsetText(values[3]), RowsetText(values[4]));
                    fkName ??= "";
                    break;
                }
            default:
                {
                    var values = BindSystemProcedureArguments(procedure, calledAs, arguments,
                    [
                        RowsetNameParameter("pk_table_schema"), RowsetNameParameter("pk_table_catalog"),
                        RowsetNameParameter("foreignkey_tab_schema"), RowsetNameParameter("foreignkey_tab_catalog"),
                    ]);
                    (pkSchema, pkCatalog, fkSchema, fkCatalog) = (RowsetText(values[0]), RowsetText(values[1]), RowsetText(values[2]), RowsetText(values[3]));
                    break;
                }
        }

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        bool Matches(string? wanted, string actual) => wanted is null || collation.Equals(wanted, actual);
        var rows = new List<SqlValue[]>();
        if (Matches(pkCatalog, database.Name) && Matches(fkCatalog, database.Name))
        {
            foreach (var item in RowsetObjects(batch, fkName, fkSchema))
            {
                if (item.Table is not { } child)
                    continue;
                foreach (var fk in child.OutgoingForeignKeys)
                {
                    var referenced = fk.ReferencedTable;
                    var referencedSchema = SchemaNameOf(database, referenced);
                    if (!Matches(pkName, referenced.Name) || !Matches(pkSchema, referencedSchema))
                        continue;
                    var keyIndexId = BuiltInResources.ResolveForeignKeyIndexId(fk);
                    var keyName = referenced.IndexIdentities().Find(identity => identity.IndexId == keyIndexId).Name;
                    for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
                    {
                        rows.Add([
                            catalog, SqlValue.FromNVarchar(RowsetName, referencedSchema), SqlValue.FromNVarchar(RowsetName, referenced.Name),
                            SqlValue.FromNVarchar(RowsetName, referenced.Columns[fk.ReferencedColumnOrdinals[i]].Name),
                            SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(SqlType.Int32),
                            catalog, SqlValue.FromNVarchar(RowsetName, item.Schema), SqlValue.FromNVarchar(RowsetName, child.Name),
                            SqlValue.FromNVarchar(RowsetName, child.Columns[fk.ChildColumnOrdinals[i]].Name),
                            SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(SqlType.Int32), SqlValue.FromInt32(i + 1),
                            SqlValue.FromNVarchar(RowsetRule, ReferentialRule(fk.UpdateAction)), SqlValue.FromNVarchar(RowsetRule, ReferentialRule(fk.DeleteAction)),
                            keyName is null ? SqlValue.Null(RowsetName) : SqlValue.FromNVarchar(RowsetName, keyName),
                            SqlValue.FromNVarchar(RowsetName, fk.Name), SqlValue.FromInt16(3),
                        ]);
                    }
                }
            }
        }
        yield return new SimulatedSqlResultSet(RowsetForeignKeysSchema, RowsetForeignKeysNames, rows);
    }

    private static string ReferentialRule(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };

    /// <summary>
    /// <c>sp_catalogs_rowset</c> / <c>…2</c>: the OLE DB <c>CATALOGS</c>
    /// rowset, the server's databases by name — the one named, or every one.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeCatalogsRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string? wanted = null;
        if (procedure == "sp_catalogs_rowset")
            wanted = RowsetText(BindSystemProcedureArguments(procedure, calledAs, arguments, [RowsetNameParameter("catalog_name", required: true)])[0]);
        else
            _ = BindSystemProcedureArguments(procedure, calledAs, arguments, []);

        var simulation = batch.Connection.Simulation;
        var names = new List<string>();
        foreach (var (name, _) in simulation.Databases)
        {
            if (procedure == "sp_catalogs_rowset2" || (wanted is not null && Collation.Get(simulation.ServerCollationName).Equals(wanted, name)))
                names.Add(name);
        }
        names.Sort(Collation.Get(simulation.ServerCollationName));
        var rows = new List<SqlValue[]>(names.Count);
        foreach (var name in names)
            rows.Add([SqlValue.FromNVarchar(RowsetName, name), SqlValue.Null(RowsetDescription)]);
        yield return new SimulatedSqlResultSet([RowsetName, RowsetDescription], ["CATALOG_NAME", "DESCRIPTION"], rows);
    }

    /// <summary>
    /// <c>sp_table_privileges_rowset</c> / <c>…2</c>: the OLE DB
    /// <c>TABLE_PRIVILEGES</c> rowset — the owner's five privileges granted
    /// by <c>dbo</c>, then each standing GRANT — the grantor and grantee
    /// leading.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeTablePrivilegesRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("table_schema"), RowsetNameParameter("grantor"), RowsetNameParameter("grantee")]
            : [RowsetNameParameter("table_name", required: true), RowsetNameParameter("table_schema"), RowsetNameParameter("grantor"), RowsetNameParameter("grantee")];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var offset = everyObject ? -1 : 0;
        var name = everyObject ? null : RowsetText(values[0]);
        var schema = RowsetText(values[1 + offset]);
        var grantorFilter = RowsetText(values[2 + offset]);
        var granteeFilter = RowsetText(values[3 + offset]);

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        var rows = new List<(string Schema, string Table, string Privilege, string Grantee, SqlValue[] Row)>();
        if (everyObject || name is not null)
        {
            foreach (var (schemaName, ownerName, objectId, tableName) in PrivilegeObjects(database))
            {
                if ((name is not null && !collation.Equals(name, tableName)) || (schema is not null && !collation.Equals(schema, schemaName)))
                    continue;
                void Add(string grantor, string grantee, string privilege, bool grantable)
                {
                    if ((grantorFilter is null || collation.Equals(grantorFilter, grantor)) && (granteeFilter is null || collation.Equals(granteeFilter, grantee)))
                    {
                        rows.Add((schemaName, tableName, privilege, grantee,
                        [
                            SqlValue.FromNVarchar(RowsetName, grantor), SqlValue.FromNVarchar(RowsetName, grantee), catalog,
                            SqlValue.FromNVarchar(RowsetName, schemaName), SqlValue.FromNVarchar(RowsetName, tableName),
                            SqlValue.FromNVarchar(RowsetType, privilege), SqlValue.FromBoolean(grantable),
                        ]));
                    }
                }
                foreach (var privilege in OwnerTablePrivileges)
                    Add(Database.DefaultSchemaName, ownerName, privilege, true);
                var seen = new HashSet<(string, string, string)>();
                foreach (var (grantor, grantee, privilege, grantable, _) in ObjectGrants(database, objectId))
                {
                    if (privilege is not null && seen.Add((grantor, grantee, privilege)))
                        Add(grantor, grantee, privilege, grantable);
                }
            }
        }
        rows.Sort((a, b) =>
        {
            var bySchema = collation.Compare(a.Schema, b.Schema);
            if (bySchema != 0)
                return bySchema;
            var byTable = collation.Compare(a.Table, b.Table);
            if (byTable != 0)
                return byTable;
            var byPrivilege = string.CompareOrdinal(a.Privilege, b.Privilege);
            return byPrivilege != 0 ? byPrivilege : collation.Compare(a.Grantee, b.Grantee);
        });
        yield return new SimulatedSqlResultSet(RowsetTablePrivilegesSchema, RowsetTablePrivilegesNames, [.. rows.Select(static r => r.Row)]);
    }

    /// <summary>
    /// <c>sp_column_privileges_rowset</c> / <c>…2</c>: the OLE DB
    /// <c>COLUMN_PRIVILEGES</c> rowset — per column the owner's INSERT /
    /// REFERENCES / SELECT / UPDATE granted by <c>dbo</c>, then each
    /// standing GRANT on the object or that column — by column, privilege
    /// and grantee.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeColumnPrivilegesRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("table_schema"), RowsetNameParameter("column_name"), RowsetNameParameter("grantor"), RowsetNameParameter("grantee")]
            : [RowsetNameParameter("table_name", required: true), RowsetNameParameter("table_schema"), RowsetNameParameter("column_name"), RowsetNameParameter("grantor"), RowsetNameParameter("grantee")];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var offset = everyObject ? -1 : 0;
        var name = everyObject ? null : RowsetText(values[0]);
        var schema = RowsetText(values[1 + offset]);
        var columnFilter = RowsetText(values[2 + offset]);
        var grantorFilter = RowsetText(values[3 + offset]);
        var granteeFilter = RowsetText(values[4 + offset]);

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        var rows = new List<(string Schema, string Table, string Column, string Privilege, string Grantee, SqlValue[] Row)>();
        if (everyObject || name is not null)
        {
            foreach (var (schemaName, ownerName, objectId, tableName) in PrivilegeObjects(database))
            {
                if ((name is not null && !collation.Equals(name, tableName)) || (schema is not null && !collation.Equals(schema, schemaName)))
                    continue;
                var grants = ObjectGrants(database, objectId).ToList();
                var columns = Parser.Expressions.ColumnProperty.ColumnsOf(database, objectId) ?? [];
                for (var i = 0; i < columns.Length; i++)
                {
                    var columnName = columns[i].Name;
                    if (columnFilter is not null && !collation.Equals(columnFilter, columnName))
                        continue;
                    var columnId = columns[i].ColumnId == 0 ? i + 1 : columns[i].ColumnId;
                    var seen = new HashSet<(string, string, string)>();
                    void Add(string grantor, string grantee, string privilege, bool grantable)
                    {
                        if (seen.Add((grantor, grantee, privilege))
                            && (grantorFilter is null || collation.Equals(grantorFilter, grantor)) && (granteeFilter is null || collation.Equals(granteeFilter, grantee)))
                        {
                            rows.Add((schemaName, tableName, columnName, privilege, grantee,
                            [
                                SqlValue.FromNVarchar(RowsetName, grantor), SqlValue.FromNVarchar(RowsetName, grantee), catalog,
                                SqlValue.FromNVarchar(RowsetName, schemaName), SqlValue.FromNVarchar(RowsetName, tableName),
                                SqlValue.FromNVarchar(RowsetName, columnName), SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(SqlType.Int32),
                                SqlValue.FromNVarchar(RowsetType, privilege), SqlValue.FromBoolean(grantable),
                            ]));
                        }
                    }
                    foreach (var privilege in OwnerColumnPrivileges)
                        Add(Database.DefaultSchemaName, ownerName, privilege, true);
                    foreach (var (grantor, grantee, privilege, grantable, minorId) in grants)
                    {
                        if (minorId == 0 || minorId == columnId)
                            Add(grantor, grantee, privilege ?? "REFERENCES", grantable);
                    }
                }
            }
        }
        rows.Sort((a, b) =>
        {
            var bySchema = collation.Compare(a.Schema, b.Schema);
            if (bySchema != 0)
                return bySchema;
            var byTable = collation.Compare(a.Table, b.Table);
            if (byTable != 0)
                return byTable;
            var byColumn = collation.Compare(a.Column, b.Column);
            if (byColumn != 0)
                return byColumn;
            var byPrivilege = string.CompareOrdinal(a.Privilege, b.Privilege);
            return byPrivilege != 0 ? byPrivilege : collation.Compare(a.Grantee, b.Grantee);
        });
        yield return new SimulatedSqlResultSet(RowsetColumnPrivilegesSchema, RowsetColumnPrivilegesNames, [.. rows.Select(static r => r.Row)]);
    }
}
